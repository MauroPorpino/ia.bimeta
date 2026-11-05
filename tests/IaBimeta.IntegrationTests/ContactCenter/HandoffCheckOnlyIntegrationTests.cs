using IaBimeta.ContactCenter.Contracts;
using IaBimeta.ContactCenter.Infrastructure;
using IaBimeta.Conversations.Contracts;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.ContactCenter;

/// <summary>
/// A verificação <c>CheckOnly</c> do handoff (ajuste da C2) sobre o ContactCenter REAL e o PostgreSQL: responde "um pedido feito agora seria aceito?" com as mesmas regras e a mesma classificação do
/// pedido de verdade, SEM gravar nada (nem item, nem recibo, nem intenção, nem transição, nem a auditoria de recusa), para o Agents só avisar o cliente de uma transferência que as regras aceitam.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class HandoffCheckOnlyIntegrationTests(PostgresFixture banco)
{
    private static Task<RequestHandoffResult> Perguntar(CenarioDeConversas c, Guid sessao, Guid? fila = null, RequestContext? contexto = null) =>
        c.ComoAsync(contexto ?? c.ContextoDeSistema, sp => sp.GetRequiredService<IHandoffRequests>().RequestHandoffAsync(new RequestHandoffCommand(sessao, Guid.NewGuid(), "ai-decision.price", fila, CheckOnly: true)));

    private static Task<RequestHandoffResult> Pedir(CenarioDeConversas c, Guid sessao, Guid? fila = null) =>
        c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<IHandoffRequests>().RequestHandoffAsync(new RequestHandoffCommand(sessao, Guid.NewGuid(), "ai-decision.price", fila)));

    /// <summary>Tudo que o ContactCenter grava por pedido, para provar que a verificação não grava nada.</summary>
    private static async Task<(int Itens, int Operacoes, int Auditoria, long Versao, HandlingMode Modo)> Rastro(CenarioDeConversas c, Guid sessao)
    {
        var contagens = await c.ComoAsync(c.ContextoDeSistema, async sp =>
        {
            var db = sp.GetRequiredService<ContactCenterDbContext>();
            // A auditoria: o que ainda espera entrega (outbox adiado) mais o que já está na trilha do tenant.
            var naTrilha = await db.Database.SqlQueryRaw<int>("select count(*)::int as \"Value\" from audit.audit_events where tenant_id = {0}", c.Tenant.TenantId.Value).SingleAsync();
            return (await db.Items.CountAsync(), await db.Operations.CountAsync(), await db.AuditOutbox.CountAsync() + naTrilha);
        });
        var estado = (await c.SessaoAsync(sessao))!;
        return (contagens.Item1, contagens.Item2, contagens.Item3, estado.StateVersion, estado.HandlingMode);
    }

    [Fact]
    public async Task Com_fila_que_atende_a_verificacao_diz_Routable_e_nao_grava_nada()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hcroteavel");
        var fila = await c.CriarFilaAsync();
        await c.CriarRegraAsync(fila.Id);
        var sessao = await c.NovaConversaAsync();
        var antes = await Rastro(c, sessao);

        var porRota = await Perguntar(c, sessao);
        var porFila = await Perguntar(c, sessao, fila.Id);

        Assert.Equal(new RequestHandoffResult(RequestHandoffOutcome.Routable), porRota);
        Assert.Equal(new RequestHandoffResult(RequestHandoffOutcome.Routable), porFila);
        Assert.Equal(antes, await Rastro(c, sessao));
        Assert.Equal(0, c.EventosDe("conversations.handling-mode-changed", sessao));
    }

    [Fact]
    public async Task Sem_fila_nem_rota_a_recusa_definitiva_e_a_mesma_do_pedido_de_verdade_e_a_verificacao_nao_audita()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hcsemrota");
        var sessao = await c.NovaConversaAsync();
        var antes = await Rastro(c, sessao);

        var verificacao = await Perguntar(c, sessao);

        Assert.Equal(new RequestHandoffResult(RequestHandoffOutcome.NotRoutable, null, null, false, "no-route"), verificacao);
        Assert.Equal(antes, await Rastro(c, sessao)); // nenhuma auditoria de recusa foi guardada pela pergunta

        // O pedido de verdade devolve o mesmo resultado e, esse sim, guarda a auditoria da recusa.
        var real = await Pedir(c, sessao);
        Assert.Equal(verificacao, real);
        Assert.True((await Rastro(c, sessao)).Auditoria > antes.Auditoria);
    }

    [Fact]
    public async Task Fila_pausada_e_recusa_transitoria_e_depois_de_retomada_a_verificacao_passa()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hcpausa");
        var fila = await c.CriarFilaAsync();
        await c.CriarRegraAsync(fila.Id);
        var sessao = await c.NovaConversaAsync();

        await c.PausarAsync(fila.Id);
        var pausada = await Perguntar(c, sessao);
        await c.RetomarAsync(fila.Id);
        var aberta = await Perguntar(c, sessao);

        Assert.Equal(new RequestHandoffResult(RequestHandoffOutcome.NotRoutable, null, null, true, "queue-paused"), pausada);
        Assert.Equal(RequestHandoffOutcome.Routable, aberta.Outcome);
    }

    [Fact]
    public async Task Fila_fora_do_horario_e_recusa_transitoria()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hchorario");
        var fila = await c.CriarFilaAsync();
        await c.CriarRegraAsync(fila.Id);
        await c.DefinirHorarioAsync(fila.Id, "UTC", HandoffTestSupport.FechadoHoje());
        await c.ConfigurarFilaAsync(fila.Id, foraDoHorario: IaBimeta.ContactCenter.Domain.OutOfHoursBehavior.Reject);
        var sessao = await c.NovaConversaAsync();

        var resultado = await Perguntar(c, sessao);

        Assert.Equal(new RequestHandoffResult(RequestHandoffOutcome.NotRoutable, null, null, true, "queue-closed"), resultado);
    }

    [Fact]
    public async Task Conversa_ja_com_atendente_humano_e_recusa_definitiva_e_conversa_encerrada_e_SessionEnded()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hchumano");
        var fila = await c.CriarFilaAsync();
        await c.CriarRegraAsync(fila.Id);
        var comHumano = await c.NovaConversaAsync();
        var encerrada = await c.NovaConversaAsync();
        await c.TransicionarAsync(comHumano, HandlingMode.HumanAssisted, Guid.NewGuid(), await c.VersaoDaSessaoAsync(comHumano));
        await c.EncerrarSessaoAsync(encerrada);

        var humano = await Perguntar(c, comHumano);
        var fim = await Perguntar(c, encerrada);

        Assert.Equal((RequestHandoffOutcome.Refused, false, "already-handled-by-human"), (humano.Outcome, humano.Retryable, humano.Code));
        Assert.Equal((RequestHandoffOutcome.SessionEnded, "session-closed"), (fim.Outcome, fim.Code));
    }

    [Fact]
    public async Task Quem_nao_pode_gerir_a_conversa_e_a_conversa_de_outro_tenant_tem_o_mesmo_resultado_session_unavailable()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hcseg");
        var outro = await CenarioDeConversas.CriarAsync(banco, "hcsegb");
        var fila = await c.CriarFilaAsync();
        await c.CriarRegraAsync(fila.Id);
        var sessao = await c.NovaConversaAsync();
        var sessaoDeOutro = await outro.NovaConversaAsync();
        var soLeitor = c.Usuario(c.OrganizacaoId, Permission.ConversationRead);

        var semPermissao = await Perguntar(c, sessao, contexto: soLeitor);
        var deOutroTenant = await Perguntar(c, sessaoDeOutro);
        var inexistente = await Perguntar(c, Guid.NewGuid());

        // Nada vaza: sem permissão, sessão de outro tenant e sessão inexistente são indistinguíveis (TEN-2).
        Assert.Equal((RequestHandoffOutcome.Refused, false, "session-unavailable"), (semPermissao.Outcome, semPermissao.Retryable, semPermissao.Code));
        Assert.Equal(semPermissao, deOutroTenant);
        Assert.Equal(semPermissao, inexistente);
    }
}
