using System.Reflection;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Events;
using IaBimeta.Messaging;
using IaBimeta.SharedKernel;
using IaBimeta.Workflows.Core.Application;
using IaBimeta.Workflows.Contracts;
using IaBimeta.Workflows.Infrastructure;

namespace IaBimeta.Workflows.Tests;

/// <summary>
/// Suspensão efetiva da empresa (Etapa 02, ADR 0043) nos handlers de eventos de fluxo: com a empresa suspensa a mensagem recebida e o conteúdo pronto são consumidos SEM escolher fluxo, acordar instância nem tocar
/// no inbox ou no armazenamento. Os colaboradores são dublês que FALHAM o teste se chamados; o inbox é nulo de propósito (qualquer toque nele quebraria o teste).
/// </summary>
public class FluxosTenantSuspensoTests
{
    private static readonly TenantId Tenant = new(Guid.Parse("a0000000-0000-0000-0000-000000000001"));
    private static readonly DateTimeOffset Agora = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    private class ProxyQueFalha : DispatchProxy
    {
        public List<string> Chamadas { get; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Chamadas.Add(targetMethod!.Name);
            throw new InvalidOperationException($"Chamada inesperada: {targetMethod.Name}");
        }
    }

    private sealed class PortaFalsa : ITenantOperationGate
    {
        public TenantOperationState Estado { get; set; } = TenantOperationState.Operational;

        public List<TenantId> Consultas { get; } = new();

        public Task<TenantOperationState> GetAsync(TenantId tenantId, CancellationToken cancellationToken = default)
        {
            Consultas.Add(tenantId);
            return Task.FromResult(Estado);
        }
    }

    private sealed class RelogioFixo : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Agora;
    }

    private static EventEnvelope Envelope(string tipo, object payload, Guid sessao) =>
        new(Guid.NewGuid(), tipo, 1, Tenant, Guid.NewGuid(), null, sessao.ToString(), Agora, payload);

    private static InboundMessageRegisteredV1 Entrada(Guid sessao) =>
        new(sessao, Guid.NewGuid(), Guid.NewGuid(), 1, MessageKind.Text, ContentState.Ready, false, new ChannelInstanceId(Guid.NewGuid()));

    private static (InboundForFlowHandler Handler, IAutomationRouter Roteador, IFlowRuntimeStore Armazenamento) EntradaCom(ITenantOperationGate? porta)
    {
        var roteador = DispatchProxy.Create<IAutomationRouter, ProxyQueFalha>();
        var armazenamento = DispatchProxy.Create<IFlowRuntimeStore, ProxyQueFalha>();
        return (new InboundForFlowHandler(null!, roteador, armazenamento, new RelogioFixo(), porta), roteador, armazenamento);
    }

    [Fact]
    public async Task Mensagem_recebida_com_empresa_suspensa_e_consumida_sem_escolher_fluxo_nem_acordar_instancia()
    {
        var porta = new PortaFalsa { Estado = new TenantOperationState(false, null) };
        var (handler, roteador, armazenamento) = EntradaCom(porta);
        var sessao = Guid.NewGuid();

        var resultado = await handler.HandleAsync(Envelope(ConversationEventTypes.InboundMessageRegistered, Entrada(sessao), sessao), default);

        Assert.Equal(HandlerOutcome.Success, resultado.Outcome);
        Assert.Empty(((ProxyQueFalha)(object)roteador).Chamadas);
        Assert.Empty(((ProxyQueFalha)(object)armazenamento).Chamadas);
        Assert.Equal(new[] { Tenant }, porta.Consultas);
    }

    [Fact]
    public async Task Conteudo_pronto_com_empresa_suspensa_e_consumido_sem_acordar_instancia()
    {
        var porta = new PortaFalsa { Estado = new TenantOperationState(false, null) };
        var armazenamento = DispatchProxy.Create<IFlowRuntimeStore, ProxyQueFalha>();
        var handler = new ContentReadyForFlowHandler(null!, armazenamento, new RelogioFixo(), porta);
        var sessao = Guid.NewGuid();

        var resultado = await handler.HandleAsync(Envelope(ConversationEventTypes.MessageContentReady, new MessageContentReadyV1(sessao, Guid.NewGuid(), 1), sessao), default);

        Assert.Equal(HandlerOutcome.Success, resultado.Outcome);
        Assert.Empty(((ProxyQueFalha)(object)armazenamento).Chamadas);
    }

    [Fact]
    public async Task Sessao_invalida_continua_sendo_morta_mesmo_com_empresa_suspensa()
    {
        var (handler, _, _) = EntradaCom(new PortaFalsa { Estado = new TenantOperationState(false, null) });

        var resultado = await handler.HandleAsync(Envelope(ConversationEventTypes.InboundMessageRegistered, Entrada(Guid.Empty), Guid.Empty), default);

        Assert.Equal(HandlerOutcome.Dead, resultado.Outcome);
        Assert.Equal("invalid-session", resultado.Reason);
    }

    [Fact]
    public async Task Mensagem_recebida_com_empresa_operacional_prossegue_para_o_inbox()
    {
        // O inbox é nulo neste teste: chegar a ele (NullReferenceException) prova que a guarda da suspensão NÃO consumiu o evento.
        var porta = new PortaFalsa();
        var (handler, _, _) = EntradaCom(porta);
        var sessao = Guid.NewGuid();

        await Assert.ThrowsAsync<NullReferenceException>(() => handler.HandleAsync(Envelope(ConversationEventTypes.InboundMessageRegistered, Entrada(sessao), sessao), default));
        Assert.Equal(new[] { Tenant }, porta.Consultas);
    }

    [Fact]
    public async Task Conteudo_pronto_com_empresa_reativada_prossegue_para_o_inbox()
    {
        var armazenamento = DispatchProxy.Create<IFlowRuntimeStore, ProxyQueFalha>();
        var handler = new ContentReadyForFlowHandler(null!, armazenamento, new RelogioFixo(), new PortaFalsa { Estado = new TenantOperationState(true, Agora.AddDays(-1)) });
        var sessao = Guid.NewGuid();

        await Assert.ThrowsAsync<NullReferenceException>(() => handler.HandleAsync(Envelope(ConversationEventTypes.MessageContentReady, new MessageContentReadyV1(sessao, Guid.NewGuid(), 1), sessao), default));
    }

    [Fact]
    public async Task Sem_porta_configurada_a_mensagem_recebida_prossegue_para_o_inbox_como_antes()
    {
        var (handler, _, _) = EntradaCom(null);
        var sessao = Guid.NewGuid();

        await Assert.ThrowsAsync<NullReferenceException>(() => handler.HandleAsync(Envelope(ConversationEventTypes.InboundMessageRegistered, Entrada(sessao), sessao), default));
    }
}
