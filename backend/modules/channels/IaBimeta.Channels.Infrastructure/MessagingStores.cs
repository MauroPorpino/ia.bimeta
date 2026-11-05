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

    public async Task<Guid?> FindAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var tenant = _context.GetRequired().TenantId;
        return await _db.Webhooks.AsNoTracking().Where(w => w.TenantId == tenant && w.AccountId == accountId).Select(w => (Guid?)w.WebhookId).FirstOrDefaultAsync(cancellationToken);
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

/// <summary>
/// O estado de envio de cada conta no banco (SND-2 e SND-6). TODA operação roda numa transação curta que TRAVA a linha da conta (<c>SELECT ... FOR UPDATE</c>), então duas instâncias do Worker nunca gastam a mesma
/// ficha nem se atropelam na pausa. A linha nasce na primeira necessidade, com o balde cheio (<c>INSERT ... ON CONFLICT DO NOTHING</c>); a chave estrangeira recusa conta que não é do tenant do contexto. O relógio é o
/// do <see cref="TimeProvider"/> (testável). Os valores vão como parâmetros; só os nomes (do modelo do EF) entram no texto do SQL.
/// </summary>
internal sealed class AccountSendStateStore : IAccountSendStateStore
{
    private readonly ChannelsDbContext _db;
    private readonly IRequestContextAccessor _context;
    private readonly ChannelSendingOptions _options;
    private readonly TimeProvider _clock;

    public AccountSendStateStore(ChannelsDbContext db, IRequestContextAccessor context, ChannelSendingOptions options, TimeProvider clock)
    {
        _db = db;
        _context = context;
        _options = options;
        _clock = clock;
    }

    public Task<SendAcquireResult> TryAcquireAsync(Guid accountId, CancellationToken cancellationToken) =>
        MutateAsync(accountId, (estado, agora) => estado.TryTake(agora, _options.DefaultThroughputPerSecond, _options.BurstSeconds), cancellationToken);

    public Task ApplyCooldownAsync(Guid accountId, TimeSpan duration, CancellationToken cancellationToken) =>
        MutateAsync(accountId, (estado, agora) =>
        {
            estado.ApplyCooldown(agora + duration, agora);
            return true;
        }, cancellationToken);

    public Task<bool> PauseAsync(Guid accountId, string reason, CancellationToken cancellationToken) =>
        MutateAsync(accountId, (estado, agora) => estado.Pause(reason, agora), cancellationToken);

    public Task<bool> ResumeAsync(Guid accountId, CancellationToken cancellationToken) =>
        MutateAsync(accountId, (estado, agora) => estado.Resume(agora), cancellationToken);

    public Task SetThroughputAsync(Guid accountId, int? perSecond, CancellationToken cancellationToken) =>
        MutateAsync(accountId, (estado, agora) =>
        {
            estado.SetThroughput(perSecond, agora);
            return true;
        }, cancellationToken);

    public Task<AccountSendState?> FindAsync(Guid accountId, CancellationToken cancellationToken) =>
        _db.SendStates.AsNoTracking().FirstOrDefaultAsync(s => s.AccountId == accountId, cancellationToken);

    private async Task<T> MutateAsync<T>(Guid accountId, Func<AccountSendState, DateTimeOffset, T> acao, CancellationToken cancellationToken)
    {
        var tenant = _context.GetRequired().TenantId;
        var agora = _clock.GetUtcNow();
        var tipo = _db.Model.FindEntityType(typeof(AccountSendState)) ?? throw new InvalidOperationException("O DbContext do Channels não mapeou o estado de envio.");
        var tabela = $"\"{tipo.GetSchema()}\".\"{tipo.GetTableName()}\"";
        var capacidadeInicial = AccountSendState.Capacity(_options.DefaultThroughputPerSecond, _options.BurstSeconds);

        // Nenhuma cópia velha da linha pode ficar rastreada neste escopo: o balde é lido sempre do banco, com a linha travada.
        foreach (var rastreada in _db.ChangeTracker.Entries<AccountSendState>().ToList())
        {
            rastreada.State = EntityState.Detached;
        }

        await using var transacao = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
#pragma warning disable EF1002, EF1003
            await _db.Database.ExecuteSqlRawAsync(
                $"INSERT INTO {tabela} (tenant_id, account_id, tokens, refilled_at, is_paused, updated_at) VALUES ({{0}}, {{1}}, {{2}}, {{3}}, false, {{3}}) ON CONFLICT (tenant_id, account_id) DO NOTHING",
                new object[] { tenant.Value, accountId, capacidadeInicial, agora },
                cancellationToken);

            var linhas = await _db.SendStates
                .FromSqlRaw($"SELECT * FROM {tabela} WHERE tenant_id = {{0}} AND account_id = {{1}} FOR UPDATE", tenant.Value, accountId)
                .IgnoreQueryFilters()
                .AsTracking()
                .ToListAsync(cancellationToken);
#pragma warning restore EF1002, EF1003
            var estado = linhas.Single();

            var resultado = acao(estado, agora);
            await _db.SaveChangesAsync(cancellationToken);
            await transacao.CommitAsync(cancellationToken);
            return resultado;
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == Npgsql.PostgresErrorCodes.ForeignKeyViolation)
        {
            // A chave estrangeira recusou: a conta não existe no tenant do contexto.
            throw new InvalidOperationException("A conta não existe no tenant do contexto.");
        }
        finally
        {
            foreach (var rastreada in _db.ChangeTracker.Entries<AccountSendState>().ToList())
            {
                rastreada.State = EntityState.Detached;
            }
        }
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
