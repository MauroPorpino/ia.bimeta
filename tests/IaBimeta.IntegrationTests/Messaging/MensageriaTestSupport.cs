using System.Collections.Concurrent;
using IaBimeta.Conversations.Infrastructure;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.Messaging;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Messaging;

/// <summary>Um evento sintético, de contrato próprio, para provar a mecânica do relay sem depender de um consumidor real.</summary>
internal sealed record EventoDeTeste(Guid Id, string Rotulo);

/// <summary>
/// Cria o contexto do tenant do evento no escopo da entrega, como o host faria (TEN-1): contexto de sistema, só com o tenant do
/// envelope. Os testes o usam em lugar do inicializador do Worker.
/// </summary>
internal sealed class EscopoDeEntregaDeTeste : IMessagingScopeInitializer
{
    public void Initialize(IServiceScope scope, TenantId tenant, string origin) =>
        scope.ServiceProvider.GetRequiredService<RequestContextHolder>().Set(new RequestContext(tenant, origin));
}

/// <summary>O que o handler de teste fez: qual evento, em qual tenant, como sistema ou não, e em que ordem global.</summary>
internal sealed record ChamadaDoManipulador(Guid EventId, Guid TenantId, bool EraSistema, string? OrderingKey, long Sequencia);

/// <summary>
/// Os roteiros do handler de teste, por <c>EventId</c>: cada entrega consome a próxima resposta do roteiro (sucesso, nova tentativa,
/// morto, exceção, ou um efeito colateral qualquer). Sem roteiro, o handler responde sucesso. Os identificadores de evento são
/// únicos por teste, então os roteiros não se misturam.
/// </summary>
internal static class RoteiroDeTeste
{
    private static readonly ConcurrentDictionary<Guid, ConcurrentQueue<Func<EventEnvelope, Task<HandlerResult>>>> Roteiros = new();
    private static readonly ConcurrentQueue<ChamadaDoManipulador> Registro = new();
    private static long _sequencia;

    public static void Definir(Guid eventId, params Func<EventEnvelope, Task<HandlerResult>>[] respostas) =>
        Roteiros[eventId] = new ConcurrentQueue<Func<EventEnvelope, Task<HandlerResult>>>(respostas);

    public static Func<EventEnvelope, Task<HandlerResult>> Sucesso { get; } = _ => Task.FromResult(HandlerResult.Success);

    public static Func<EventEnvelope, Task<HandlerResult>> Repetir(TimeSpan? depois = null, string? motivo = "transitorio") =>
        _ => Task.FromResult(HandlerResult.Retry(depois, motivo));

    public static Func<EventEnvelope, Task<HandlerResult>> Morto(string motivo) => _ => Task.FromResult(HandlerResult.Dead(motivo));

    public static Func<EventEnvelope, Task<HandlerResult>> Excecao(Exception falha) => _ => throw falha;

    public static bool TentarProxima(Guid eventId, out Func<EventEnvelope, Task<HandlerResult>>? resposta)
    {
        resposta = null;
        return Roteiros.TryGetValue(eventId, out var fila) && fila.TryDequeue(out resposta);
    }

    public static void Registrar(Guid eventId, Guid tenantId, bool eraSistema, string? chave) =>
        Registro.Enqueue(new ChamadaDoManipulador(eventId, tenantId, eraSistema, chave, Interlocked.Increment(ref _sequencia)));

    public static IReadOnlyList<ChamadaDoManipulador> De(Guid eventId) => Registro.Where(c => c.EventId == eventId).OrderBy(c => c.Sequencia).ToList();
}

/// <summary>O handler de teste do tipo <c>test.event</c>: registra a chamada (com o contexto que viu) e obedece ao roteiro do evento.</summary>
internal sealed class ManipuladorDeTeste : EventHandlerBase<EventoDeTeste>
{
    public const string Tipo = "test.event";

    private readonly IRequestContextAccessor _contexto;

    public ManipuladorDeTeste(IRequestContextAccessor contexto)
    {
        _contexto = contexto;
    }

    public override string EventType => Tipo;

    public override int EventVersion => 1;

    protected override async Task<HandlerResult> HandleAsync(EventEnvelope envelope, EventoDeTeste payload, CancellationToken cancellationToken)
    {
        var atual = _contexto.Current;
        RoteiroDeTeste.Registrar(envelope.EventId, atual?.TenantId.Value ?? Guid.Empty, atual?.IsSystem ?? false, envelope.OrderingKey);
        return RoteiroDeTeste.TentarProxima(envelope.EventId, out var resposta) ? await resposta!(envelope) : HandlerResult.Success;
    }
}

/// <summary>Atalhos para gravar eventos no outbox, rodar o relay e inspecionar o outbox do Conversations, na composição de mensageria.</summary>
internal static class MensageriaTestSupport
{
    public static OutboxRelay<ConversationsDbContext> Relay(this CenarioDeConversas c) =>
        c.Banco.ServicosComMensageria.GetRequiredService<OutboxRelay<ConversationsDbContext>>();

    /// <summary>Um ciclo completo do relay (até não haver mais nada elegível). O relógio de teste decide o que já está elegível.</summary>
    public static Task<RelayRunResult> RodarRelayAsync(this CenarioDeConversas c, string dono = "teste") => c.Relay().RunOnceAsync(dono);

    private static Task<T> NoBanco<T>(this CenarioDeConversas c, Func<ConversationsDbContext, Task<T>> acao) => c.ComoAsync(
        c.ContextoDeSistema, sp => acao(sp.GetRequiredService<ConversationsDbContext>()));

    /// <summary>Grava um evento sintético no outbox, como o módulo emissor faria na própria transação.</summary>
    public static async Task<Guid> PublicarEventoDeTesteAsync(this CenarioDeConversas c, string chaveDeOrdem, Guid? id = null, string rotulo = "x", Guid? tenantId = null)
    {
        var eventId = id ?? Guid.NewGuid();
        var tenant = tenantId is null ? c.Tenant.TenantId : new TenantId(tenantId.Value);
        await c.ComoAsync(c.ContextoDeSistema, async sp =>
        {
            var db = sp.GetRequiredService<ConversationsDbContext>();
            var escritor = sp.GetRequiredService<IOutboxWriter>();
            var agora = sp.GetRequiredService<TimeProvider>().GetUtcNow();
            escritor.Stage(
                db,
                new[] { new EventEnvelope(eventId, ManipuladorDeTeste.Tipo, 1, tenant, Guid.NewGuid(), null, chaveDeOrdem, agora, new EventoDeTeste(eventId, rotulo)) },
                tenant,
                agora);
            await db.SaveChangesAsync();
            return 0;
        });
        return eventId;
    }

    public static Task<OutboxMessage?> LinhaDoOutboxAsync(this CenarioDeConversas c, Guid eventId) =>
        c.NoBanco(db => db.Set<OutboxMessage>().AsNoTracking().FirstOrDefaultAsync(o => o.Id == eventId));

    public static Task<List<OutboxMessage>> LinhasDoOutboxDaSessaoAsync(this CenarioDeConversas c, Guid sessao) =>
        c.NoBanco(db => db.Set<OutboxMessage>().AsNoTracking().Where(o => o.OrderingKey == $"session:{sessao}").OrderBy(o => o.IngestSeq).ToListAsync());

    public static Task<List<OutboxMessage>> LinhasDoOutboxDoTenantAsync(this CenarioDeConversas c) =>
        c.NoBanco(db => db.Set<OutboxMessage>().AsNoTracking().Where(o => o.TenantId == c.Tenant.TenantId.Value).OrderBy(o => o.IngestSeq).ToListAsync());

    /// <summary>Muda a linha do outbox por fora do relay, para simular um processo que caiu ou um lease que venceu.</summary>
    public static async Task AlterarOutboxAsync(this CenarioDeConversas c, Guid eventId, Action<OutboxMessage> alteracao)
    {
        await c.NoBanco(async db =>
        {
            var linha = await db.Set<OutboxMessage>().SingleAsync(o => o.Id == eventId);
            alteracao(linha);
            await db.SaveChangesAsync();
            return 0;
        });
    }

    /// <summary>Roda um comando SQL no banco do Conversations (preparar ou desfazer uma condição de falha do próprio teste).</summary>
    public static async Task ExecutarSqlAsync(this CenarioDeConversas c, string sql)
    {
        await c.NoBanco(async db =>
        {
            await db.Database.ExecuteSqlRawAsync(sql);
            return 0;
        });
    }

    /// <summary>Quantos registros o inbox do ContactCenter tem para o evento (0 ou 1).</summary>
    public static Task<int> RegistrosDoInboxAsync(this CenarioDeConversas c, Guid eventId) => c.ComoAsync(
        c.ContextoDeSistema,
        sp => sp.GetRequiredService<IaBimeta.ContactCenter.Infrastructure.ContactCenterDbContext>().Set<InboxMessage>().AsNoTracking().CountAsync(m => m.EventId == eventId));
}
