using System.Reflection;
using IaBimeta.Channels.Application;
using IaBimeta.Channels.Contracts;
using IaBimeta.Channels.Contracts.Providers;
using IaBimeta.Channels.Domain;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Contracts.Events;
using IaBimeta.Conversations.Contracts.Queries;
using IaBimeta.SharedKernel;

namespace IaBimeta.Channels.Tests;

/// <summary>
/// Suspensão efetiva da empresa (Etapa 02, ADR 0043) no Sender: empresa suspensa ADIA a entrega sem gravar intenção nem tocar no provedor; o pedido anterior à reativação falha de forma definitiva e visível, sem
/// chamar o provedor; o posterior segue o caminho normal. Os colaboradores que não podem ser tocados nestes cenários são dublês que FALHAM o teste se chamados.
/// </summary>
public class OutboundTenantSuspensoTests
{
    private static readonly TenantId Tenant = new(Guid.Parse("a0000000-0000-0000-0000-000000000001"));
    private static readonly DateTimeOffset Agora = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    // ---- dublês ----

    /// <summary>Qualquer chamada a um colaborador que não deveria ser tocado derruba o teste (e fica registrada).</summary>
    private class ProxyQueFalha : DispatchProxy
    {
        public List<string> Chamadas { get; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Chamadas.Add(targetMethod!.Name);
            throw new InvalidOperationException($"Chamada inesperada: {targetMethod.Name}");
        }
    }

    /// <summary>Grava as chamadas e devolve o padrão (para a porta de ingestão do Conversations, cujo resultado o Sender ignora).</summary>
    private class ProxyQueGrava : DispatchProxy
    {
        public List<(string Metodo, object? Argumento)> Chamadas { get; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Chamadas.Add((targetMethod!.Name, args is { Length: > 0 } ? args[0] : null));
            var retorno = targetMethod.ReturnType;
            if (retorno == typeof(Task))
            {
                return Task.CompletedTask;
            }

            if (retorno.IsGenericType && retorno.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var tipo = retorno.GetGenericArguments()[0];
                var padrao = tipo.IsValueType ? Activator.CreateInstance(tipo) : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(tipo).Invoke(null, new[] { padrao });
            }

            return retorno.IsValueType && retorno != typeof(void) ? Activator.CreateInstance(retorno) : null;
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

    private sealed class DespachoEmMemoria : IOutboundMessageStore
    {
        public List<OutboundMessage> Itens { get; } = new();

        public int Gravacoes { get; private set; }

        public Task<OutboundMessage?> FindByOperationAsync(Guid operationId, CancellationToken cancellationToken) =>
            Task.FromResult(Itens.FirstOrDefault(m => m.OperationId == operationId));

        public Task<OutboundMessage?> FindByProviderMessageIdAsync(string providerMessageId, CancellationToken cancellationToken) =>
            Task.FromResult(Itens.FirstOrDefault(m => m.ProviderMessageId == providerMessageId));

        public Task<bool> TryAddAsync(OutboundMessage message, CancellationToken cancellationToken)
        {
            Itens.Add(message);
            Gravacoes++;
            return Task.FromResult(true);
        }

        public Task SaveAsync(CancellationToken cancellationToken)
        {
            Gravacoes++;
            return Task.CompletedTask;
        }
    }

    private sealed class ConsultaDeDespacho : IOutboundDispatchQueries
    {
        public int Chamadas { get; private set; }

        public Task<OutboundDispatchView?> GetAsync(Guid messageId, CancellationToken cancellationToken = default)
        {
            Chamadas++;
            return Task.FromResult<OutboundDispatchView?>(null); // "message-not-found": prova que o fluxo passou da guarda da suspensão, sem precisar montar o canal inteiro.
        }
    }

    private sealed class Cenario
    {
        public Cenario(ITenantOperationGate? porta)
        {
            Porta = porta;
            Despacho = new DespachoEmMemoria();
            Consulta = new ConsultaDeDespacho();
            Ingestao = DispatchProxy.Create<IConversationIngestion, ProxyQueGrava>();
            Provedor = DispatchProxy.Create<IChannelProviderAdapter, ProxyQueFalha>();
            var repositorio = DispatchProxy.Create<IChannelRepository, ProxyQueFalha>();
            var protetor = DispatchProxy.Create<ISecretProtector, ProxyQueFalha>();
            var estadoDeEnvio = DispatchProxy.Create<IAccountSendStateStore, ProxyQueFalha>();
            var arquivos = DispatchProxy.Create<IOutboundAttachmentReader, ProxyQueFalha>();
            var auditoria = DispatchProxy.Create<IAuditWriter, ProxyQueFalha>();
            Repositorio = repositorio;
            Handler = new OutboundMessageRequestedHandler(
                Consulta,
                arquivos,
                Ingestao,
                repositorio,
                protetor,
                new ChannelProviderAdapters(Array.Empty<IChannelProviderAdapter>()),
                Despacho,
                estadoDeEnvio,
                new ChannelSendingOptions(),
                auditoria,
                new RelogioFixo(Agora),
                porta);
        }

        public ITenantOperationGate? Porta { get; }

        public DespachoEmMemoria Despacho { get; }

        public ConsultaDeDespacho Consulta { get; }

        public IConversationIngestion Ingestao { get; }

        public IChannelProviderAdapter Provedor { get; }

        public IChannelRepository Repositorio { get; }

        public OutboundMessageRequestedHandler Handler { get; }

        public IEnumerable<(string Metodo, object? Argumento)> StatusGravados => ((ProxyQueGrava)(object)Ingestao).Chamadas;

        public (EventEnvelope Envelope, OutboundMessageRequestedV1 Payload) Pedido(DateTimeOffset ocorridoEm)
        {
            var payload = new OutboundMessageRequestedV1(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, new ChannelInstanceId(Guid.NewGuid()));
            var envelope = new EventEnvelope(Guid.NewGuid(), Handler.EventType, 1, Tenant, Guid.NewGuid(), null, payload.SessionId.ToString(), ocorridoEm, payload);
            return (envelope, payload);
        }
    }

    private sealed class RelogioFixo : TimeProvider
    {
        private readonly DateTimeOffset _agora;

        public RelogioFixo(DateTimeOffset agora) => _agora = agora;

        public override DateTimeOffset GetUtcNow() => _agora;
    }

    // ---- cenários ----

    [Fact]
    public async Task Empresa_suspensa_adia_a_entrega_sem_gravar_intencao_sem_consultar_a_mensagem_e_sem_chamar_o_provedor()
    {
        var c = new Cenario(new PortaFalsa { Estado = new TenantOperationState(false, null) });
        var (envelope, _) = c.Pedido(Agora);

        var resultado = await c.Handler.HandleAsync(envelope, default);

        Assert.Equal(HandlerOutcome.Defer, resultado.Outcome);
        Assert.Equal(TimeSpan.FromMinutes(2), resultado.RetryAfter);
        Assert.Equal("tenant-suspended", resultado.Reason);
        Assert.Empty(c.Despacho.Itens);
        Assert.Equal(0, c.Despacho.Gravacoes);
        Assert.Equal(0, c.Consulta.Chamadas);
        Assert.Empty(c.StatusGravados); // nada informado ao Conversations: a mensagem segue pendente, não falha
        Assert.Empty(((ProxyQueFalha)(object)c.Provedor).Chamadas);
    }

    [Fact]
    public async Task Empresa_suspensa_consulta_a_porta_com_o_tenant_do_envelope()
    {
        var porta = new PortaFalsa { Estado = new TenantOperationState(false, null) };
        var c = new Cenario(porta);
        var (envelope, _) = c.Pedido(Agora);

        await c.Handler.HandleAsync(envelope, default);

        Assert.Equal(new[] { Tenant }, porta.Consultas);
    }

    [Fact]
    public async Task Pedido_anterior_a_reativacao_falha_de_forma_definitiva_e_visivel_sem_chamar_o_provedor()
    {
        var c = new Cenario(new PortaFalsa { Estado = new TenantOperationState(true, Agora) });
        var (envelope, payload) = c.Pedido(Agora.AddHours(-3));

        var resultado = await c.Handler.HandleAsync(envelope, default);

        Assert.Equal(HandlerOutcome.Success, resultado.Outcome);
        var despacho = Assert.Single(c.Despacho.Itens);
        Assert.Equal(OutboundState.Failed, despacho.State);
        Assert.Equal("tenant-suspended", despacho.ErrorCode);
        Assert.Equal(0, despacho.Attempts);
        Assert.Equal(0, c.Consulta.Chamadas);
        Assert.Empty(((ProxyQueFalha)(object)c.Provedor).Chamadas);

        // O Conversations é informado da falha (visível ao atendente, que pode reenviar por ação autorizada).
        var informado = Assert.Single(c.StatusGravados);
        Assert.Equal(nameof(IConversationIngestion.RecordDeliveryStatusAsync), informado.Metodo);
        var comando = Assert.IsType<RecordDeliveryStatusCommand>(informado.Argumento);
        Assert.Equal(payload.MessageId, comando.MessageId);
        Assert.Equal(DeliveryState.Failed, comando.State);
        Assert.Equal("send-failed.tenant-suspended", comando.FailureReason);
    }

    [Fact]
    public async Task Pedido_posterior_a_reativacao_nao_e_barrado_pela_guarda_da_suspensao()
    {
        var c = new Cenario(new PortaFalsa { Estado = new TenantOperationState(true, Agora.AddHours(-3)) });
        var (envelope, _) = c.Pedido(Agora);

        await c.Handler.HandleAsync(envelope, default);

        // Passou da guarda: a mensagem foi consultada no Conversations (aqui inexistente, então falha por outro motivo, nunca por suspensão).
        Assert.Equal(1, c.Consulta.Chamadas);
        var despacho = Assert.Single(c.Despacho.Itens);
        Assert.NotEqual("tenant-suspended", despacho.ErrorCode);
        Assert.Equal("message-not-found", despacho.ErrorCode);
    }

    [Fact]
    public async Task Empresa_operacional_que_nunca_foi_suspensa_segue_o_caminho_normal()
    {
        var c = new Cenario(new PortaFalsa());
        var (envelope, _) = c.Pedido(Agora.AddDays(-30));

        await c.Handler.HandleAsync(envelope, default);

        Assert.Equal(1, c.Consulta.Chamadas);
        Assert.Equal("message-not-found", Assert.Single(c.Despacho.Itens).ErrorCode);
    }

    [Fact]
    public async Task Pedido_no_exato_instante_da_reativacao_nao_e_considerado_anterior()
    {
        var c = new Cenario(new PortaFalsa { Estado = new TenantOperationState(true, Agora) });
        var (envelope, _) = c.Pedido(Agora);

        await c.Handler.HandleAsync(envelope, default);

        Assert.Equal("message-not-found", Assert.Single(c.Despacho.Itens).ErrorCode);
    }

    [Fact]
    public async Task Sem_porta_configurada_o_envio_segue_como_antes()
    {
        var c = new Cenario(null);
        var (envelope, _) = c.Pedido(Agora.AddDays(-30));

        await c.Handler.HandleAsync(envelope, default);

        Assert.Equal(1, c.Consulta.Chamadas);
        Assert.Equal("message-not-found", Assert.Single(c.Despacho.Itens).ErrorCode);
    }

    [Fact]
    public async Task Reativada_a_empresa_a_mesma_entrega_adiada_antes_e_tratada_como_anterior_a_reativacao()
    {
        // Um evento adiado durante a suspensão volta depois da reativação: como nasceu antes dela, falha de forma visível em vez de sair em bloco.
        var porta = new PortaFalsa { Estado = new TenantOperationState(false, null) };
        var c = new Cenario(porta);
        var (envelope, _) = c.Pedido(Agora.AddMinutes(-30));

        var adiado = await c.Handler.HandleAsync(envelope, default);
        porta.Estado = new TenantOperationState(true, Agora);
        var depois = await c.Handler.HandleAsync(envelope, default);

        Assert.Equal(HandlerOutcome.Defer, adiado.Outcome);
        Assert.Equal(HandlerOutcome.Success, depois.Outcome);
        Assert.Equal("tenant-suspended", Assert.Single(c.Despacho.Itens).ErrorCode);
        Assert.Empty(((ProxyQueFalha)(object)c.Provedor).Chamadas);
    }
}
