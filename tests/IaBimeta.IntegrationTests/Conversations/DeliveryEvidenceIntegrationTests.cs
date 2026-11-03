using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Contracts.Events;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Conversations;

/// <summary>
/// A evidência de entrega (fatia A1; ADR 0011, seção F; DP-08), com PostgreSQL real: a declaração humana (<c>EvidenceSource.Manual</c>) só CONFIRMA o envio
/// de uma mensagem em <c>SendUnknown</c>, nunca produz falha, expiração, entrega nem leitura, e nunca provoca reenvio. A origem padrão do provedor segue
/// exatamente como era. Nenhuma operação nova de gateway existe aqui: o caminho do atendente até esta declaração é da fatia A7.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DeliveryEvidenceIntegrationTests(PostgresFixture banco)
{
    private const string Contato = "5511999990001";

    /// <summary>Uma sessão com uma resposta da IA aceita (a mensagem de saída) cujo envio ficou incerto: Requested, Queued, Sending e SendUnknown.</summary>
    private async Task<(CenarioDeConversas C, IngestInboundMessageResult Entrada, Guid Saida)> ComEnvioIncertoAsync(string prefixo)
    {
        var c = await CenarioDeConversas.CriarAsync(banco, prefixo);
        var r = await c.EntrarAsync(Contato, "Pergunta");
        var turno = Guid.NewGuid();
        var inicio = await c.IniciarTurnoAsync(r.SessionId, turno);
        var resposta = await c.ResponderAsync(r.SessionId, turno, inicio, r.ConversationId);
        var saida = resposta.MessageId!.Value;

        Assert.Equal(DeliveryStatusOutcome.Applied, (await c.EntregaAsync(saida, DeliveryState.Queued)).Outcome);
        Assert.Equal(DeliveryStatusOutcome.Applied, (await c.EntregaAsync(saida, DeliveryState.Sending)).Outcome);
        Assert.Equal(DeliveryStatusOutcome.Applied, (await c.EntregaAsync(saida, DeliveryState.SendUnknown)).Outcome);
        return (c, r, saida);
    }

    private static Task<DeliveryStatusResult> EvidenciaAsync(CenarioDeConversas c, Guid mensagem, DeliveryState estado, EvidenceSource origem, string? motivo = null) =>
        c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<IConversationIngestion>().RecordDeliveryStatusAsync(
            new RecordDeliveryStatusCommand(mensagem, estado, motivo, origem)));

    private static async Task<DeliveryState?> EstadoDaSaidaAsync(CenarioDeConversas c, Guid sessao) =>
        (await c.HistoricoAsync(sessao))!.Items.Last().DeliveryState;

    [Fact]
    public async Task A_declaracao_humana_confirma_o_envio_incerto_sem_provocar_reenvio_nem_evento_de_falha()
    {
        var (c, r, saida) = await ComEnvioIncertoAsync("manualok");
        Assert.Equal(DeliveryState.SendUnknown, await EstadoDaSaidaAsync(c, r.SessionId));
        var pedidosAntes = c.EventosDe(ConversationEventTypes.OutboundMessageRequested, r.SessionId);

        var confirmacao = await EvidenciaAsync(c, saida, DeliveryState.Sent, EvidenceSource.Manual);

        Assert.Equal((DeliveryStatusOutcome.Applied, (DeliveryState?)DeliveryState.Sent), (confirmacao.Outcome, confirmacao.CurrentState));
        Assert.Equal(DeliveryState.Sent, await EstadoDaSaidaAsync(c, r.SessionId));
        var sessao = (await c.SessaoAsync(r.SessionId))!;
        Assert.False(sessao.ReplyDeliveryFailed);
        Assert.Equal(1, sessao.AnsweredThroughSeq);
        Assert.False(sessao.HasActiveTurn);
        // Nenhum reenvio (nenhum pedido de envio novo), nenhuma falha sinalizada e nenhum turno de IA.
        Assert.Equal(pedidosAntes, c.EventosDe(ConversationEventTypes.OutboundMessageRequested, r.SessionId));
        Assert.Equal(0, c.EventosDe(ConversationEventTypes.OutboundMessageFailed, r.SessionId));
        Assert.Equal(1, c.EventosDe(ConversationEventTypes.InboundMessageRegistered, r.SessionId));
    }

    [Fact]
    public async Task A_declaracao_humana_nunca_produz_falha_expiracao_entrega_nem_leitura_e_o_envio_incerto_continua()
    {
        var (c, r, saida) = await ComEnvioIncertoAsync("manualnao");

        foreach (var estado in new[] { DeliveryState.Failed, DeliveryState.Expired, DeliveryState.Dead, DeliveryState.Delivered, DeliveryState.Read })
        {
            var recusa = await EvidenciaAsync(c, saida, estado, EvidenceSource.Manual, "conferi-no-console");
            Assert.Equal((DeliveryStatusOutcome.EvidenceNotAllowed, (DeliveryState?)null), (recusa.Outcome, recusa.CurrentState));
        }

        // Nada mudou: o envio continua incerto, a sessão não foi marcada com falha e nada foi sinalizado ao ContactCenter.
        Assert.Equal(DeliveryState.SendUnknown, await EstadoDaSaidaAsync(c, r.SessionId));
        Assert.False((await c.SessaoAsync(r.SessionId))!.ReplyDeliveryFailed);
        Assert.Equal(0, c.EventosDe(ConversationEventTypes.OutboundMessageFailed, r.SessionId));
    }

    [Fact]
    public async Task A_declaracao_humana_e_idempotente_e_nunca_regride_um_status_mais_avancado()
    {
        var (c, r, saida) = await ComEnvioIncertoAsync("manualidem");

        Assert.Equal(DeliveryStatusOutcome.Applied, (await EvidenciaAsync(c, saida, DeliveryState.Sent, EvidenceSource.Manual)).Outcome);
        var repetida = await EvidenciaAsync(c, saida, DeliveryState.Sent, EvidenceSource.Manual);
        Assert.Equal((DeliveryStatusOutcome.Ignored, (DeliveryState?)DeliveryState.Sent), (repetida.Outcome, repetida.CurrentState));

        // O provedor entrega depois: a declaração humana feita antes não impede o status mais avançado, e uma nova declaração não o faz regredir.
        Assert.Equal(DeliveryStatusOutcome.Applied, (await c.EntregaAsync(saida, DeliveryState.Delivered)).Outcome);
        var tardia = await EvidenciaAsync(c, saida, DeliveryState.Sent, EvidenceSource.Manual);
        Assert.Equal((DeliveryStatusOutcome.Ignored, (DeliveryState?)DeliveryState.Delivered), (tardia.Outcome, tardia.CurrentState));
        Assert.Equal(DeliveryState.Delivered, await EstadoDaSaidaAsync(c, r.SessionId));
    }

    [Fact]
    public async Task A_evidencia_do_provedor_continua_saindo_do_envio_incerto_pela_transicao_valida_para_Sent()
    {
        var (c, r, saida) = await ComEnvioIncertoAsync("provedorsent");

        // Sem informar a origem (o chamador de antes da A1) e com a origem explícita: o comportamento é o mesmo.
        var semOrigem = await c.EntregaAsync(saida, DeliveryState.Sent);

        Assert.Equal((DeliveryStatusOutcome.Applied, (DeliveryState?)DeliveryState.Sent), (semOrigem.Outcome, semOrigem.CurrentState));
        var explicita = await EvidenciaAsync(c, saida, DeliveryState.Sent, EvidenceSource.ProviderStatus);
        Assert.Equal(DeliveryStatusOutcome.Ignored, explicita.Outcome);
        Assert.Equal(DeliveryState.Sent, await EstadoDaSaidaAsync(c, r.SessionId));
    }

    [Fact]
    public async Task A_rejeicao_definitiva_do_provedor_continua_produzindo_falha_a_partir_do_envio_incerto()
    {
        var (c, r, saida) = await ComEnvioIncertoAsync("provedorfalha");

        var falha = await EvidenciaAsync(c, saida, DeliveryState.Failed, EvidenceSource.ProviderRejection, "recipient-unreachable");

        Assert.Equal((DeliveryStatusOutcome.Applied, (DeliveryState?)DeliveryState.Failed), (falha.Outcome, falha.CurrentState));
        Assert.True((await c.SessaoAsync(r.SessionId))!.ReplyDeliveryFailed);
        Assert.Equal(1, c.EventosDe(ConversationEventTypes.OutboundMessageFailed, r.SessionId));
    }

    [Fact]
    public async Task Depois_de_falha_definitiva_nem_a_declaracao_humana_a_desfaz()
    {
        var (c, r, saida) = await ComEnvioIncertoAsync("falhaterminal");
        Assert.Equal(DeliveryStatusOutcome.Applied, (await c.EntregaAsync(saida, DeliveryState.Failed, "recipient-unreachable")).Outcome);

        var tentativa = await EvidenciaAsync(c, saida, DeliveryState.Sent, EvidenceSource.Manual);

        Assert.Equal((DeliveryStatusOutcome.Ignored, (DeliveryState?)DeliveryState.Failed), (tentativa.Outcome, tentativa.CurrentState));
        Assert.Equal(DeliveryState.Failed, await EstadoDaSaidaAsync(c, r.SessionId));
    }

    [Fact]
    public async Task A_declaracao_humana_so_vale_para_mensagem_de_saida_e_so_no_proprio_tenant()
    {
        var (c, r, saida) = await ComEnvioIncertoAsync("manualiso");
        var outro = await CenarioDeConversas.CriarAsync(banco, "manualoutro");

        var deEntrada = await EvidenciaAsync(c, r.MessageId, DeliveryState.Sent, EvidenceSource.Manual);
        var deOutroTenant = await EvidenciaAsync(outro, saida, DeliveryState.Sent, EvidenceSource.Manual);
        var inexistente = await EvidenciaAsync(c, Guid.NewGuid(), DeliveryState.Sent, EvidenceSource.Manual);

        Assert.Equal(DeliveryStatusOutcome.NotAnOutboundMessage, deEntrada.Outcome);
        Assert.Equal(DeliveryStatusOutcome.MessageNotFound, deOutroTenant.Outcome);
        Assert.Equal(DeliveryStatusOutcome.MessageNotFound, inexistente.Outcome);
        Assert.Equal(DeliveryState.SendUnknown, await EstadoDaSaidaAsync(c, r.SessionId));
    }
}
