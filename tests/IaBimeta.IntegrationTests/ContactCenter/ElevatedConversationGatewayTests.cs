using IaBimeta.ContactCenter.Application;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Contracts.Queries;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.ContactCenter;

/// <summary>
/// A elevação de contexto do host (ADR 0009): o atendente comum não tem permissão de conversa por organização, e só a porta do host
/// executa a chamada com o contexto de SISTEMA do mesmo tenant, num escopo próprio. A elevação não vaza para a requisição, o tenant é sempre
/// o do contexto confiável em curso e a superfície da porta é fechada. PostgreSQL real.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ElevatedConversationGatewayTests(PostgresFixture banco)
{
    [Fact]
    public async Task O_atendente_comum_nao_le_nem_muda_a_conversa_pelo_Conversations_mas_a_porta_do_host_executa_por_ele()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "elevacao");
        var fila = await c.CriarFilaAsync();
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var sessao = await c.NovaConversaAsync();

        await c.ComoAsync(ana.Contexto, async sp =>
        {
            // Direto no Conversations, o atendente não tem permissão de conversa por organização: a sessão responde como inexistente.
            Assert.Null(await sp.GetRequiredService<IConversationQueries>().GetSessionAsync(sessao));

            // Pela porta do host, a chamada acontece com o contexto de sistema do mesmo tenant.
            var porta = sp.GetRequiredService<IConversationGateway>();
            var vista = await porta.GetSessionAsync(sessao, default);
            Assert.Equal(sessao, vista!.Id);

            // E a requisição NÃO ficou elevada: o contexto dela é o mesmo de antes, com o mesmo usuário, e a leitura direta continua negada.
            var atual = sp.GetRequiredService<IRequestContextAccessor>().GetRequired();
            Assert.False(atual.IsSystem);
            Assert.Equal(ana.Id, atual.Principal!.UserId);
            Assert.Null(await sp.GetRequiredService<IConversationQueries>().GetSessionAsync(sessao));
            return 0;
        });
    }

    [Fact]
    public async Task Sem_contexto_confiavel_a_porta_falha_fechada_e_nao_ha_tenant_a_elevar()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "elevasem");
        var sessao = await c.NovaConversaAsync();

        await Assert.ThrowsAsync<TenantContextRequiredException>(() =>
            c.ComoAsync(null, sp => sp.GetRequiredService<IConversationGateway>().GetSessionAsync(sessao, default)));
    }

    [Fact]
    public async Task O_tenant_e_sempre_o_do_contexto_em_curso_chamadas_paralelas_de_tenants_diferentes_nao_se_misturam()
    {
        var a = await CenarioDeConversas.CriarAsync(banco, "elevaa");
        var b = await CenarioDeConversas.CriarAsync(banco, "elevab");
        var sessaoA = await a.NovaConversaAsync();
        var sessaoB = await b.NovaConversaAsync();

        var tarefas = Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
        {
            var daA = await a.ComoAsync(a.ContextoDeSistema, sp => sp.GetRequiredService<IConversationGateway>().GetSessionAsync(i % 2 == 0 ? sessaoA : sessaoB, default));
            var daB = await b.ComoAsync(b.ContextoDeSistema, sp => sp.GetRequiredService<IConversationGateway>().GetSessionAsync(i % 2 == 0 ? sessaoA : sessaoB, default));
            return (i, daA?.Id, daB?.Id);
        })).ToList();

        foreach (var (i, doTenantA, doTenantB) in await Task.WhenAll(tarefas))
        {
            // Cada tenant só enxerga a sessão que é dele, qualquer que seja a que se pergunte: o tenant nunca vem de parâmetro.
            Assert.Equal(i % 2 == 0 ? sessaoA : (Guid?)null, doTenantA);
            Assert.Equal(i % 2 == 0 ? (Guid?)null : sessaoB, doTenantB);
        }
    }

    [Fact]
    public void A_superficie_da_porta_e_fechada_seis_operacoes_a_sexta_so_le_e_nenhuma_generica()
    {
        var metodos = typeof(IConversationGateway).GetMethods().Select(m => m.Name).OrderBy(n => n).ToArray();

        // As cinco operações do ADR 0009 mais a consulta de efeito (somente leitura): nenhuma operação genérica de código privilegiado.
        Assert.Equal(
            new[] { "FenceAsync", "GetEffectAsync", "GetHistoryAsync", "GetSessionAsync", "TransferAsync", "TransitionAsync" },
            metodos);
    }

    [Fact]
    public async Task A_porta_executa_o_comando_do_estado_para_o_atendente_comum_que_o_Conversations_recusaria_direto()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "elevacmd");
        var ana = await c.AgenteNaFilaAsync(banco, (await c.CriarFilaAsync()).Id);
        var sessao = await c.NovaConversaAsync();
        var versao = await c.VersaoDaSessaoAsync(sessao);

        await c.ComoAsync(ana.Contexto, async sp =>
        {
            var direto = await sp.GetRequiredService<IHandlingCommands>().TransitionHandlingModeAsync(
                new TransitionHandlingModeCommand(sessao, HandlingMode.HumanAssisted, null, versao, Guid.NewGuid(), "manual"));
            Assert.Equal(TransitionOutcome.SessionNotFound, direto.Outcome); // sem permissão de conversa, a sessão "não existe" para ele

            var pelaPorta = await sp.GetRequiredService<IConversationGateway>().TransitionAsync(
                new TransitionHandlingModeCommand(sessao, HandlingMode.HumanAssisted, null, versao, Guid.NewGuid(), "manual"), default);
            Assert.Equal(TransitionOutcome.Applied, pelaPorta.Outcome);
            return 0;
        });
    }
}
