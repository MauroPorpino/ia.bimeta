using IaBimeta.Api.Operations;
using IaBimeta.Conversations.Contracts;
using IaBimeta.IntegrationTests.Api;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.IntegrationTests.ContactCenter;
using IaBimeta.IntegrationTests.Messaging;
using IaBimeta.Workflows.Contracts;
using IaBimeta.Workflows.Core.Domain;
using IaBimeta.Workflows.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Workflows;

/// <summary>O procedimento de drenagem (<c>workflows status</c> e <c>workflows drain --confirm</c>): só encerra o que está ativo, só da empresa pedida, devolve a conversa ao agente e descarta o que ia ser enviado.</summary>
[Collection(PostgresCollection.Name)]
public sealed class DrenagemDeFluxosTests(PostgresFixture banco)
{
    private sealed record Semeado(Guid Espera, Guid Aguarda, Guid Concluida, Guid SessaoEspera);

    private async Task<Semeado> SemearAsync(IaBimeta.Tenancy.Contracts.TenantInfo tenant, Guid org)
    {
        await using var escopo = banco.NovoEscopo(PostgresFixture.Contexto(tenant));
        var db = escopo.ServiceProvider.GetRequiredService<WorkflowsDbContext>();
        var agora = DateTimeOffset.UtcNow;
        FlowInstance Nova(FlowStatus status)
        {
            var i = FlowInstance.Create(tenant.TenantId, Guid.NewGuid(), org, Guid.NewGuid(), "drenar", 1, null, OnFailurePolicy.Agent, "v1:x", "k1", agora.AddHours(-2));
            i.Apply(new FlowState(status, "menu", new Dictionary<string, string>(), 0, 1, null, status == FlowStatus.AwaitingEffect ? "1:0" : null), "v1:x", "k1", 1, status == FlowStatus.AwaitingEffect, agora);
            return i;
        }

        var espera = Nova(FlowStatus.WaitingInput);
        var aguarda = Nova(FlowStatus.AwaitingEffect);
        var concluida = Nova(FlowStatus.Completed);
        db.Instances.AddRange(espera, aguarda, concluida);
        foreach (var i in new[] { espera, aguarda, concluida })
        {
            db.Decisions.Add(RoutingDecision.Create(tenant.TenantId, i.SessionId, AutomationOwner.Flow, null, agora));
        }

        db.Effects.Add(FlowEffectRecord.Create(tenant.TenantId, Guid.NewGuid(), aguarda.Id, "1:0", 1, 0, EffectKind.SendMessage, "menu", "v1:conteudo", "k1", agora));
        await db.SaveChangesAsync();
        return new Semeado(espera.Id, aguarda.Id, concluida.Id, espera.SessionId);
    }

    [Fact]
    public async Task Status_so_le_e_a_drenagem_exige_confirmacao_e_so_encerra_as_ativas_da_empresa_pedida()
    {
        var a = await banco.RegistrarTenantAsync("drena-a");
        var b = await banco.RegistrarTenantAsync("drena-b");
        var sa = await SemearAsync(a, (await banco.CriarOrganizacaoAsync(a, "Org A")).Id);
        var sb = await SemearAsync(b, (await banco.CriarOrganizacaoAsync(b, "Org B")).Id);
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());

        Assert.Equal(2, await WorkflowsCommand.RunAsync(["drain"], new StringWriter())); // sem --confirm
        Assert.Equal(2, await WorkflowsCommand.RunAsync(["bogus"], new StringWriter()));

        var saida = new StringWriter();
        Assert.Equal(0, await WorkflowsCommand.ExecuteAsync(api.Services, drain: false, a.Slug, saida));
        Assert.Contains("WaitingInput", saida.ToString());
        Assert.Contains("AwaitingEffect", saida.ToString());
        Assert.DoesNotContain("Completed", saida.ToString());

        var drenagem = new StringWriter();
        Assert.Equal(0, await WorkflowsCommand.ExecuteAsync(api.Services, drain: true, a.Slug, drenagem));
        Assert.Contains("2 instância(s) encerrada(s)", drenagem.ToString());

        async Task<(FlowInstance Espera, FlowInstance Aguarda, FlowInstance Concluida, AutomationOwner Dono, EffectState Efeito)> LerAsync(IaBimeta.Tenancy.Contracts.TenantInfo t, Semeado s)
        {
            await using var escopo = banco.NovoEscopo(PostgresFixture.Contexto(t));
            var db = escopo.ServiceProvider.GetRequiredService<WorkflowsDbContext>();
            return (await db.Instances.AsNoTracking().SingleAsync(i => i.Id == s.Espera), await db.Instances.AsNoTracking().SingleAsync(i => i.Id == s.Aguarda), await db.Instances.AsNoTracking().SingleAsync(i => i.Id == s.Concluida),
                (await db.Decisions.AsNoTracking().SingleAsync(d => d.SessionId == s.SessaoEspera)).Owner, (await db.Effects.AsNoTracking().SingleAsync()).State);
        }

        var (espera, aguarda, concluida, dono, efeito) = await LerAsync(a, sa);
        Assert.All(new[] { espera, aguarda }, i => Assert.Equal((FlowStatus.Cancelled, "operator-drain", (DateTimeOffset?)null), (i.Status, i.FailureCode, i.NextRunAt)));
        Assert.NotNull(espera.EndedAt);
        Assert.Equal(FlowStatus.Completed, concluida.Status); // o que já tinha terminado não é tocado
        Assert.Equal(AutomationOwner.Agent, dono); // a conversa volta ao agente
        Assert.Equal(EffectState.Superseded, efeito); // o que ia ser enviado é descartado

        // Outra empresa: intacta.
        var (esperaB, aguardaB, _, donoB, efeitoB) = await LerAsync(b, sb);
        Assert.Equal((FlowStatus.WaitingInput, FlowStatus.AwaitingEffect, AutomationOwner.Flow, EffectState.Pending), (esperaB.Status, aguardaB.Status, donoB, efeitoB));

        // Repetir é seguro (nada ativo na empresa A) e empresa inexistente falha sem alterar nada.
        var repetida = new StringWriter();
        Assert.Equal(0, await WorkflowsCommand.ExecuteAsync(api.Services, drain: true, a.Slug, repetida));
        Assert.Contains("0 instância(s) encerrada(s)", repetida.ToString());
        Assert.Equal(1, await WorkflowsCommand.ExecuteAsync(api.Services, drain: true, "empresa-que-nao-existe", new StringWriter()));
    }

    /// <summary>
    /// A conversa que o fluxo possuía volta ao agente COM o aviso do Conversations (<c>flow-released-to-agent</c>): sem ele o Agents só reavaliaria na próxima mensagem do cliente, e uma mensagem recebida enquanto o fluxo era
    /// o dono (e que ele, já cancelado, nunca respondeu) ficaria sem resposta. Um aviso por conversa, nenhum para outra empresa e nenhum ao repetir. O que já foi enviado ao canal (efeito concluído) não é tocado.
    /// </summary>
    [Fact]
    public async Task A_drenagem_avisa_o_agente_uma_vez_por_conversa_e_so_da_empresa_pedida_e_nao_toca_no_que_ja_foi_enviado()
    {
        banco.RelogioDaMensageria.Sincronizar();
        var a = await CenarioDeConversas.CriarAsync(banco, "drena-aviso-a", mensageria: true);
        var b = await CenarioDeConversas.CriarAsync(banco, "drena-aviso-b", mensageria: true);
        var sessaoA = await a.NovaConversaAsync();
        var sessaoB = await b.NovaConversaAsync();
        var enviadoA = Guid.Empty;

        async Task SemearAsync(CenarioDeConversas c, Guid sessao, bool comEfeitoEnviado)
        {
            await using var escopo = banco.NovoEscopo(PostgresFixture.Contexto(c.Tenant));
            var db = escopo.ServiceProvider.GetRequiredService<WorkflowsDbContext>();
            var agora = DateTimeOffset.UtcNow;
            var i = FlowInstance.Create(c.Tenant.TenantId, sessao, c.OrganizacaoId, Guid.NewGuid(), "aviso", 1, null, OnFailurePolicy.Agent, "v1:x", "k1", agora.AddHours(-1));
            i.Apply(new FlowState(FlowStatus.WaitingInput, "menu", new Dictionary<string, string>(), 0, 1, null, null), "v1:x", "k1", 1, false, agora);
            db.Instances.Add(i);
            db.Decisions.Add(RoutingDecision.Create(c.Tenant.TenantId, sessao, AutomationOwner.Flow, null, agora));
            if (comEfeitoEnviado)
            {
                var enviado = FlowEffectRecord.Create(c.Tenant.TenantId, Guid.NewGuid(), i.Id, "1:0", 1, 0, EffectKind.SendMessage, "menu", "v1:conteudo", "k1", agora);
                enviadoA = enviado.Id;
                enviado.Complete(EffectState.Done, "ok", agora);
                db.Effects.Add(enviado);
            }

            await db.SaveChangesAsync();
        }

        await SemearAsync(a, sessaoA, comEfeitoEnviado: true);
        await SemearAsync(b, sessaoB, comEfeitoEnviado: false);
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());

        static int Avisos(IEnumerable<IaBimeta.Messaging.OutboxMessage> linhas) => linhas.Count(l => l.EventType == "conversations.flow-released-to-agent");
        Assert.Equal(0, Avisos(await a.LinhasDoOutboxDaSessaoAsync(sessaoA)));

        var saida = new StringWriter();
        Assert.Equal(0, await WorkflowsCommand.ExecuteAsync(api.Services, drain: true, a.Tenant.Slug, saida));
        Assert.Contains("1 instância(s) encerrada(s)", saida.ToString());
        Assert.Equal(1, Avisos(await a.LinhasDoOutboxDaSessaoAsync(sessaoA))); // o agente foi avisado
        Assert.Equal(0, Avisos(await b.LinhasDoOutboxDaSessaoAsync(sessaoB))); // a outra empresa, não

        // O efeito que JÁ foi enviado continua "concluído": a drenagem nunca o reabre nem o reenvia.
        await using (var escopo = banco.NovoEscopo(PostgresFixture.Contexto(a.Tenant)))
        {
            var efeito = await escopo.ServiceProvider.GetRequiredService<WorkflowsDbContext>().Effects.AsNoTracking().SingleAsync(e => e.Id == enviadoA);
            Assert.Equal(EffectState.Done, efeito.State);
        }

        // Repetir não avisa de novo (nada ativo): idempotente.
        Assert.Equal(0, await WorkflowsCommand.ExecuteAsync(api.Services, drain: true, a.Tenant.Slug, new StringWriter()));
        Assert.Equal(1, Avisos(await a.LinhasDoOutboxDaSessaoAsync(sessaoA)));
    }
}
