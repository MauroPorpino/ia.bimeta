using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.ContactCenter.Infrastructure;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.IntegrationTests.Conversations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.ContactCenter;

/// <summary>
/// A identidade do período que o item prende ao concluir a ENTRADA vem da operação ORIGINAL (o recibo da transição que o Conversations gravou, conferido contra o
/// período esperado da base), nunca do período CORRENTE da sessão: humano → IA → humano é OUTRO atendimento, com o mesmo modo e o mesmo responsável. Sem o recibo
/// da própria operação (a sessão já estava no estado pedido por outra via), o item só se prende ao período que a base AUTORIZOU, e apenas se a sessão ainda está
/// nele; do contrário a identidade fica desconhecida e o acesso fechado. A reprodução é determinística: Enter aplicado, resposta perdida, outro atendimento
/// começa, e a reconciliação recupera a intenção original. PostgreSQL real.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class EnterIdentityRecoveryTests(PostgresFixture banco)
{
    private sealed record Cena(CenarioDeConversas C, ServiceQueueView Fila, Agente Supervisor, Guid Sessao);

    private async Task<Cena> MontarAsync(string prefixo, HandlingMode modoInicial = HandlingMode.AiAutonomous)
    {
        var c = await CenarioDeConversas.CriarAsync(banco, prefixo);
        var fila = await c.CriarFilaAsync("A");
        var supervisor = await c.AgenteNaFilaAsync(banco, fila.Id, QueueMemberRole.Supervisor);
        var sessao = await c.NovaConversaAsync(modoInicial: modoInicial);
        return new Cena(c, fila, supervisor, sessao);
    }

    private static Task<QueueItem> LinhaAsync(Cena s, Guid item) =>
        s.C.ComoAsync(s.C.ContextoDeSistema, sp => sp.GetRequiredService<ContactCenterDbContext>().Items.AsNoTracking().SingleAsync(i => i.Id == item));

    /// <summary>Outro atendimento começa por fora: humano → IA → humano (em espera). O período avança, o modo e o responsável são de novo os de antes.</summary>
    private static async Task OutroAtendimentoComecaAsync(Cena s)
    {
        Assert.Equal(TransitionOutcome.Applied, (await s.C.TransicionarAsync(s.Sessao, HandlingMode.AiAutonomous, null, await s.C.VersaoDaSessaoAsync(s.Sessao))).Outcome);
        Assert.Equal(TransitionOutcome.Applied, (await s.C.TransicionarAsync(s.Sessao, HandlingMode.HumanAssisted, null, await s.C.VersaoDaSessaoAsync(s.Sessao))).Outcome);
    }

    /// <summary>O supervisor da fila lê o conteúdo do item; vazio (ou negado) é "sem conteúdo".</summary>
    private static async Task<IReadOnlyList<string?>> LerAsync(Cena s, Guid item)
    {
        try
        {
            return (await s.C.MensagensDoItemAsync(item, s.Supervisor.Contexto))?.Items.Select(m => m.Text).ToList() ?? new List<string?>();
        }
        catch (QueueAccessDeniedException)
        {
            return new List<string?>();
        }
    }

    // T9: a reprodução do atalho "o estado já é o pedido" (a entrada a partir da IA)

    [Fact]
    public async Task T9_a_entrada_aplicada_com_a_resposta_perdida_nao_adota_o_periodo_do_segundo_atendimento_ao_ser_recuperada()
    {
        var s = await MontarAsync("enterid1");
        var pedido = Guid.NewGuid();

        // O Enter é aplicado no Conversations (a conversa passa a atendimento humano pendente: período 1) e a resposta se perde: a intenção fica pendente.
        var interrompida = await s.C.NaExecucaoAsync(
            s.C.ContextoDeSistema,
            g => g.Depois(nameof(IConversationGateway.TransitionAsync), () => throw new InvalidOperationException("a resposta se perdeu")),
            svc => svc.RequestHandoffAsync(s.Sessao, s.Fila.Id, pedido, "ai-requested"));
        Assert.Equal(OperationOutcome.Pending, interrompida.Outcome);
        var item = interrompida.Item.Id;
        Assert.Equal((HandlingMode.HumanAssisted, 1L), ((await s.C.SessaoAsync(s.Sessao))!.HandlingMode, (await s.C.SessaoAsync(s.Sessao))!.HandlingPeriod));

        // OUTRO atendimento começa antes da recuperação (humano → IA → humano, em espera): o estado de agora é o mesmo que o pedido, e o período é o 2.
        await OutroAtendimentoComecaAsync(s);
        Assert.Equal(2L, (await s.C.SessaoAsync(s.Sessao))!.HandlingPeriod);

        // A reconciliação recupera a intenção ORIGINAL. O recibo da transição que ela gravou diz o período dela (1), que a base confirma (0 + 1): é esse o que o
        // item prende, e não o 2 de agora (o atalho antigo concluía "porque o estado já é o pedido" com o período corrente).
        await s.C.LiberarRecuoAsync(item);
        Assert.Equal(1, await s.C.ReconciliarAsync());

        var linha = await LinhaAsync(s, item);
        Assert.Null((await s.C.ItemAsync(item))!.Pending);
        Assert.Equal((QueueItemStatus.Waiting, 1L), (linha.Status, linha.HandlingPeriod));
        Assert.NotEqual((await s.C.SessaoAsync(s.Sessao))!.HandlingPeriod, linha.HandlingPeriod);

        // O item não é o atendimento de agora: o acesso dele à conversa fica fechado.
        Assert.Empty(await LerAsync(s, item));
    }

    // T9: o controle "a entrada já autorizada em espera humana"

    [Fact]
    public async Task T9_a_entrada_ja_autorizada_em_espera_humana_prende_o_periodo_autorizado_e_o_item_le_a_conversa()
    {
        var s = await MontarAsync("enterid2", HandlingMode.HumanAssisted);
        var periodo = (await s.C.SessaoAsync(s.Sessao))!.HandlingPeriod;
        Assert.True(periodo >= 1);

        var resultado = await s.C.PedirAtendimentoAsync(s.Sessao, s.Fila.Id);

        // A sessão JÁ estava no estado pedido (sem recibo "com efeito"): a identidade é a que a base AUTORIZOU, e a sessão continua nela.
        Assert.Equal(OperationOutcome.Completed, resultado.Outcome);
        var linha = await LinhaAsync(s, resultado.Item.Id);
        Assert.Equal(periodo, linha.HandlingPeriod);
        Assert.NotEmpty(await LerAsync(s, resultado.Item.Id));
    }

    [Fact]
    public async Task T9_a_entrada_autorizada_em_espera_humana_nao_adota_o_periodo_de_outro_atendimento_que_comecou_depois()
    {
        var s = await MontarAsync("enterid3", HandlingMode.HumanAssisted);
        var autorizado = await s.C.ComoAsync(s.C.ContextoDeSistema, async sp =>
        {
            var visao = (await sp.GetRequiredService<IConversationGateway>().GetSessionAsync(s.Sessao, default))!;
            var itens = sp.GetRequiredService<IQueueItemRepository>();
            var pedido = Guid.NewGuid();
            var agora = DateTimeOffset.UtcNow;

            // A intenção de entrada, com a base de quem a autorizou: a conversa em espera humana, no período lido.
            var novo = QueueItem.Enter(
                s.C.Tenant.TenantId, visao.Id, visao.OrganizationId, visao.BusinessUnitId, visao.DepartmentId, s.Fila.Id, pedido, null, "handoff", agora,
                basis: new HandlingBasis(visao.HandlingPeriod, visao.ResponderRef, false, visao.PlacementEpoch));
            await itens.AddItemAsync(novo, QueueOperation.Begin(s.C.Tenant.TenantId, pedido, OperationKind.Enter, novo.Id, visao.Id, null, s.Fila.Id, null, agora), default);
            return (Item: novo.Id, Periodo: visao.HandlingPeriod);
        });

        // Antes de a intenção ser executada, humano → IA → humano: o mesmo modo e o mesmo responsável (ninguém), mas OUTRO atendimento.
        await OutroAtendimentoComecaAsync(s);
        Assert.Equal(autorizado.Periodo + 1, (await s.C.SessaoAsync(s.Sessao))!.HandlingPeriod);

        // A sessão está no estado pedido, mas não no período que a base autorizou, e não há recibo da operação: o item NÃO adota o período de agora.
        Assert.Equal(1, await s.C.ReconciliarAsync());
        var linha = await LinhaAsync(s, autorizado.Item);
        Assert.Null(linha.HandlingPeriod);
        Assert.Empty(await LerAsync(s, autorizado.Item));
    }
}
