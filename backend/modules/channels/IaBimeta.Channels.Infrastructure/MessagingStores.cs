using IaBimeta.Channels.Application;
using IaBimeta.Channels.Contracts;
using IaBimeta.Channels.Domain;
using IaBimeta.Channels.Infrastructure.Configurations;
using IaBimeta.Messaging;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.Channels.Infrastructure;

/// <summary>
/// O ponto de entrada do webhook de cada conta. A resolução roda SEM contexto de tenant (é a única consulta do Receiver antes de saber o tenant): <c>IgnoreQueryFilters</c> vale para a consulta toda e só as
/// chaves de roteamento saem dela (tenant, conta e provedor).
/// </summary>
internal sealed class ChannelWebhookStore : IChannelWebhookStore
{
    private readonly ChannelsDbContext _db;
    private readonly IRequestContextAccessor _context;
    private readonly TimeProvider _clock;

    public ChannelWebhookStore(ChannelsDbContext db, IRequestContextAccessor context, TimeProvider clock)
    {
        _db = db;
        _context = context;
        _clock = clock;
    }

    public async Task<WebhookTarget?> ResolveAsync(Guid webhookId, CancellationToken cancellationToken)
    {
        var linha = await (
            from webhook in _db.Webhooks.IgnoreQueryFilters().AsNoTracking()
            join conta in _db.Accounts.IgnoreQueryFilters().AsNoTracking()
                on new { webhook.TenantId, Id = webhook.AccountId } equals new { conta.TenantId, conta.Id }
            where webhook.WebhookId == webhookId
            select new { webhook.TenantId, webhook.AccountId, conta.Provider })
            .FirstOrDefaultAsync(cancellationToken);

        return linha is null ? null : new WebhookTarget(linha.TenantId, linha.AccountId, linha.Provider);
    }

    public async Task<Guid> EnsureAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var tenant = _context.GetRequired().TenantId;

        // A conta precisa existir no tenant do contexto (o filtro global garante): conta de outro tenant responde como inexistente.
        if (!await _db.Accounts.AsNoTracking().AnyAsync(a => a.Id == accountId, cancellationToken))
        {
            throw new InvalidOperationException("A conta não existe no tenant do contexto.");
        }

        var existente = await _db.Webhooks.AsNoTracking().Where(w => w.TenantId == tenant && w.AccountId == accountId).Select(w => (Guid?)w.WebhookId).FirstOrDefaultAsync(cancellationToken);
        if (existente is { } id)
        {
            return id;
        }

        var novo = ChannelWebhook.For(tenant, accountId, _clock.GetUtcNow());
        _db.Webhooks.Add(novo);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return novo.WebhookId;
        }
        catch (DbUpdateException)
        {
            // Outra requisição criou o identificador da mesma conta ao mesmo tempo: vale o que ficou gravado.
            _db.Entry(novo).State = EntityState.Detached;
            return await _db.Webhooks.AsNoTracking().Where(w => w.TenantId == tenant && w.AccountId == accountId).Select(w => w.WebhookId).SingleAsync(cancellationToken);
        }
    }
}

/// <summary>
/// O Inbox durável: o <c>outbox_message</c> do próprio módulo (o relay do Messaging o entrega). Um <c>INSERT ... ON CONFLICT DO NOTHING</c> por evento, todos na mesma transação (REC-4), então o mesmo evento
/// repetido é uma linha só. O nome da tabela vem do modelo do EF, e os valores vão como parâmetros. Só grava eventos do tenant do contexto.
/// </summary>
internal sealed class InboundEventWriter : IInboundEventWriter
{
    private readonly ChannelsDbContext _db;
    private readonly IRequestContextAccessor _context;
    private readonly TimeProvider _clock;

    public InboundEventWriter(ChannelsDbContext db, IRequestContextAccessor context, TimeProvider clock)
    {
        _db = db;
        _context = context;
        _clock = clock;
    }

    public async Task<int> WriteAsync(IReadOnlyList<InboundEventRecord> events, CancellationToken cancellationToken)
    {
        var tenant = _context.GetRequired().TenantId;
        var tipo = _db.Model.FindEntityType(typeof(OutboxMessage)) ?? throw new InvalidOperationException("O DbContext do Channels não mapeou o outbox (ConfigureOutbox).");
        var tabela = $"\"{tipo.GetSchema()}\".\"{tipo.GetTableName()}\"";
        var agora = _clock.GetUtcNow();

        await using var transacao = await _db.Database.BeginTransactionAsync(cancellationToken);
        var novos = 0;
        foreach (var evento in events)
        {
#pragma warning disable EF1002, EF1003
            var gravadas = await _db.Database.ExecuteSqlRawAsync(
                $"INSERT INTO {tabela} (tenant_id, id, ordering_key, event_type, event_version, correlation_id, causation_id, occurred_at, payload, state, attempts, next_attempt_at, created_at) " +
                "VALUES ({0}, {1}, {2}, {3}, 1, {4}, NULL, {5}, {6}::jsonb, 1, 0, {7}, {7}) ON CONFLICT (tenant_id, id) DO NOTHING",
                new object[] { tenant.Value, evento.EventId, evento.OrderingKey, evento.EventType, evento.EventId, evento.OccurredAt, MessagingJson.Serialize(evento.Payload), agora },
                cancellationToken);
#pragma warning restore EF1002, EF1003
            novos += gravadas;
        }

        await transacao.CommitAsync(cancellationToken);
        return novos;
    }
}

internal sealed class OutboundMessageStore : IOutboundMessageStore
{
    private readonly ChannelsDbContext _db;

    public OutboundMessageStore(ChannelsDbContext db)
    {
        _db = db;
    }

    public Task<OutboundMessage?> FindByOperationAsync(Guid operationId, CancellationToken cancellationToken) =>
        _db.OutboundMessages.FirstOrDefaultAsync(m => m.OperationId == operationId, cancellationToken);

    public Task<OutboundMessage?> FindByProviderMessageIdAsync(string providerMessageId, CancellationToken cancellationToken) =>
        _db.OutboundMessages.AsNoTracking().FirstOrDefaultAsync(m => m.ProviderMessageId == providerMessageId, cancellationToken);

    public async Task<bool> TryAddAsync(OutboundMessage message, CancellationToken cancellationToken)
    {
        _db.OutboundMessages.Add(message);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex, OutboundMessageConfiguration.UniqueOperationIndex) || ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            _db.Entry(message).State = EntityState.Detached;
            return false;
        }
    }

    public Task SaveAsync(CancellationToken cancellationToken) => _db.SaveChangesAsync(cancellationToken);
}
