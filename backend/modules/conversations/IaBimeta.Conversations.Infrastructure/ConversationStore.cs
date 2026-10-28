using IaBimeta.Conversations.Application;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Domain;
using IaBimeta.Conversations.Infrastructure.Configurations;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.Conversations.Infrastructure;

/// <summary>
/// Leituras do Conversations. Toda consulta passa pelo filtro global do <see cref="ConversationsDbContext"/>: só enxerga o
/// tenant do contexto e, sem contexto, falha fechada (TEN-2 e TEN-3). Quem muda estado usa a unidade de trabalho.
/// </summary>
internal sealed class ConversationStore : IConversationStore
{
    private readonly ConversationsDbContext _db;

    public ConversationStore(ConversationsDbContext db)
    {
        _db = db;
    }

    public async Task<IConversationUnitOfWork> BeginAsync(CancellationToken cancellationToken) =>
        await ConversationUnitOfWork.StartAsync(_db, cancellationToken);

    public async Task<SessionRow?> FindSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var linha = await (
            from s in _db.Sessions.AsNoTracking()
            join c in _db.Contacts.AsNoTracking() on s.ContactId equals c.Id
            where s.Id == sessionId
            select new { Sessao = s, c.DisplayName, c.PreferredLocale })
            .SingleOrDefaultAsync(cancellationToken);
        if (linha is null)
        {
            return null;
        }

        var conversa = await _db.Conversations.AsNoTracking()
            .Where(x => x.SessionId == sessionId && x.ChannelInstanceId == linha.Sessao.ChannelInstanceId)
            .Select(x => new { x.EffectiveLocale, x.LocaleSource })
            .FirstOrDefaultAsync(cancellationToken);

        return new SessionRow(linha.Sessao, linha.DisplayName, linha.PreferredLocale, conversa?.EffectiveLocale, conversa?.LocaleSource);
    }

    public async Task<IReadOnlyList<SessionRow>> ListSessionsAsync(SessionListFilter filter, CancellationToken cancellationToken)
    {
        // A comparação de linha (last_message_at, id) < (cursor) é o que dá paginação estável por chave. O filtro global de
        // tenant continua valendo por cima do SQL direto.
        IQueryable<ServiceSession> sessoes = filter.After is { } cursor
            ? _db.Sessions.FromSqlInterpolated(
                $"SELECT * FROM conversations.service_sessions WHERE (last_message_at, id) < ({cursor.LastMessageAt}, {cursor.Id})")
            : _db.Sessions;

        sessoes = sessoes.AsNoTracking();
        if (filter.OrganizationIds is { } organizacoes)
        {
            var ids = organizacoes.ToList();
            sessoes = sessoes.Where(s => ids.Contains(s.OrganizationId));
        }

        if (filter.Status is { } estado)
        {
            sessoes = sessoes.Where(s => s.Status == estado);
        }

        var linhas = await (
            from s in sessoes.OrderByDescending(s => s.LastMessageAt).ThenByDescending(s => s.Id).Take(filter.Take)
            join c in _db.Contacts.AsNoTracking() on s.ContactId equals c.Id
            select new { Sessao = s, c.DisplayName, c.PreferredLocale })
            .ToListAsync(cancellationToken);

        // O idioma efetivo vem da conversa de cada sessão (no canal dela), buscado de uma vez para a página inteira.
        var idsDaPagina = linhas.Select(l => l.Sessao.Id).ToList();
        var conversas = await _db.Conversations.AsNoTracking()
            .Where(x => idsDaPagina.Contains(x.SessionId))
            .Select(x => new { x.SessionId, x.ChannelInstanceId, x.EffectiveLocale, x.LocaleSource })
            .ToListAsync(cancellationToken);

        // A junção não garante a ordem: reaplica.
        return linhas
            .OrderByDescending(l => l.Sessao.LastMessageAt).ThenByDescending(l => l.Sessao.Id)
            .Select(l =>
            {
                var conversa = conversas.FirstOrDefault(x => x.SessionId == l.Sessao.Id && x.ChannelInstanceId == l.Sessao.ChannelInstanceId);
                return new SessionRow(l.Sessao, l.DisplayName, l.PreferredLocale, conversa?.EffectiveLocale, conversa?.LocaleSource);
            })
            .ToList();
    }

    public async Task<IReadOnlyList<Message>> ListMessagesAsync(Guid sessionId, MessagePageFilter filter, CancellationToken cancellationToken)
    {
        var mensagens = _db.Messages.AsNoTracking().Where(m => m.SessionId == sessionId);

        if (filter.Order == HistoryOrder.OldestFirst)
        {
            if (filter.After is { } apos)
            {
                mensagens = mensagens.Where(m => m.OccurredAt > apos.OccurredAt || (m.OccurredAt == apos.OccurredAt && m.MessageSeq > apos.MessageSeq));
            }

            return await mensagens.OrderBy(m => m.OccurredAt).ThenBy(m => m.MessageSeq).Take(filter.Take).ToListAsync(cancellationToken);
        }

        if (filter.After is { } antes)
        {
            mensagens = mensagens.Where(m => m.OccurredAt < antes.OccurredAt || (m.OccurredAt == antes.OccurredAt && m.MessageSeq < antes.MessageSeq));
        }

        return await mensagens.OrderByDescending(m => m.OccurredAt).ThenByDescending(m => m.MessageSeq).Take(filter.Take).ToListAsync(cancellationToken);
    }

    public Task<Message?> FindMessageByProviderEventAsync(ChannelInstanceId channelInstanceId, string providerEventId, CancellationToken cancellationToken) =>
        _db.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.ChannelInstanceId == channelInstanceId && m.ProviderEventId == providerEventId, cancellationToken);

    public Task<Message?> FindMessageAsync(Guid sessionId, Guid messageId, CancellationToken cancellationToken) =>
        _db.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.SessionId == sessionId && m.Id == messageId, cancellationToken);

    public async Task<IReadOnlyList<MessageTranslation>> ListTranslationsAsync(Guid messageId, CancellationToken cancellationToken) =>
        await _db.Translations.AsNoTracking()
            .Where(t => t.MessageId == messageId)
            .OrderBy(t => t.CreatedAt).ThenBy(t => t.Id)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<SessionTransfer>> ListTransfersAsync(Guid sessionId, CancellationToken cancellationToken) =>
        await _db.Transfers.AsNoTracking()
            .Where(t => t.SessionId == sessionId)
            .OrderBy(t => t.StateVersionAfter).ThenBy(t => t.TransferredAt)
            .ToListAsync(cancellationToken);
}

/// <summary>
/// Uma transação sobre a sessão. A linha da sessão é travada com <c>SELECT ... FOR UPDATE</c> ANTES de ser lida, então os
/// comandos sobre a mesma sessão se enfileiram: a ordem das mensagens é total e o comparar-e-trocar das cercas não tem
/// corrida. Criar contato, sessão e conversa usa <c>INSERT ... ON CONFLICT DO NOTHING</c>, que resolve a corrida sem
/// derrubar a transação (uma violação de índice a abortaria no PostgreSQL).
/// </summary>
internal sealed class ConversationUnitOfWork : IConversationUnitOfWork
{
    private readonly ConversationsDbContext _db;
    private readonly Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction _transacao;
    private bool _confirmada;

    private ConversationUnitOfWork(ConversationsDbContext db, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transacao)
    {
        _db = db;
        _transacao = transacao;
    }

    public static async Task<ConversationUnitOfWork> StartAsync(ConversationsDbContext db, CancellationToken cancellationToken)
    {
        // Cada unidade começa sem entidades rastreadas: nenhum valor lido antes da trava pode sobreviver a ela.
        db.ChangeTracker.Clear();
        var transacao = await db.Database.BeginTransactionAsync(cancellationToken);
        return new ConversationUnitOfWork(db, transacao);
    }

    public async Task<Contact> GetOrCreateContactAsync(ChannelInstanceId channelInstanceId, string externalRef, string? displayName, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var tenant = _db.CurrentTenantValue;
        var id = Guid.CreateVersion7();
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $@"INSERT INTO conversations.contacts (tenant_id, id, channel_instance_id, external_ref, display_name, created_at)
               VALUES ({tenant}, {id}, {channelInstanceId.Value}, {externalRef}, {displayName}, {now})
               ON CONFLICT (tenant_id, channel_instance_id, external_ref) DO NOTHING",
            cancellationToken);

        return await _db.Contacts.AsNoTracking().SingleAsync(c => c.ChannelInstanceId == channelInstanceId && c.ExternalRef == externalRef, cancellationToken);
    }

    public async Task<Guid> GetOrCreateOpenSessionIdAsync(Contact contact, Guid organizationId, Guid? businessUnitId, Guid? departmentId, HandlingMode initialHandlingMode, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var tenant = _db.CurrentTenantValue;
        var id = Guid.CreateVersion7();
        var modo = (short)initialHandlingMode;
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $@"INSERT INTO conversations.service_sessions
                 (tenant_id, id, organization_id, business_unit_id, department_id, contact_id, channel_instance_id, status, handling_mode, responder_ref, state_version,
                  last_message_seq, last_inbound_seq, answered_through_seq, active_turn_id, turn_epoch, last_lease_token, last_reply_accepted_at,
                  reply_delivery_failed, attention_raised_for_seq, created_at, last_message_at, closed_at)
               VALUES
                 ({tenant}, {id}, {organizationId}, {businessUnitId}, {departmentId}, {contact.Id}, {contact.ChannelInstanceId.Value}, 1, {modo}, NULL, 1,
                  0, 0, 0, NULL, 0, 0, NULL, FALSE, NULL, {now}, {now}, NULL)
               ON CONFLICT (tenant_id, contact_id) WHERE status = 1 DO NOTHING",
            cancellationToken);

        return await _db.Sessions.AsNoTracking()
            .Where(s => s.ContactId == contact.Id && s.Status == SessionStatus.Open)
            .Select(s => s.Id)
            .SingleAsync(cancellationToken);
    }

    public async Task<ServiceSession?> LockSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var tenant = _db.CurrentTenantValue;
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM conversations.service_sessions WHERE tenant_id = {tenant} AND id = {sessionId} FOR UPDATE",
            cancellationToken);

        // Lida DEPOIS da trava: o que se vê aqui é o estado atual, e ninguém o muda até o fim da transação.
        return await _db.Sessions.SingleOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
    }

    public async Task<(ServiceSession Session, Message Message)?> LockMessageAsync(Guid messageId, CancellationToken cancellationToken)
    {
        var dono = await _db.Messages.AsNoTracking()
            .Where(m => m.Id == messageId)
            .Select(m => new { m.SessionId })
            .SingleOrDefaultAsync(cancellationToken);
        if (dono is null)
        {
            return null;
        }

        var sessao = await LockSessionAsync(dono.SessionId, cancellationToken);
        if (sessao is null)
        {
            return null;
        }

        var mensagem = await _db.Messages.SingleAsync(m => m.Id == messageId, cancellationToken);
        return (sessao, mensagem);
    }

    public Task<Conversation?> FindSessionConversationAsync(ServiceSession session, CancellationToken cancellationToken) =>
        _db.Conversations.FirstOrDefaultAsync(c => c.SessionId == session.Id && c.ChannelInstanceId == session.ChannelInstanceId, cancellationToken);

    public async Task<Conversation> GetOrCreateConversationAsync(ServiceSession session, DateTimeOffset now, Locale effectiveLocale, LocaleSource localeSource, CancellationToken cancellationToken)
    {
        var tenant = _db.CurrentTenantValue;
        var id = Guid.CreateVersion7();
        var idioma = effectiveLocale.ToString();
        var origem = (short)localeSource;
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $@"INSERT INTO conversations.conversations (tenant_id, id, session_id, contact_id, channel_instance_id, created_at, effective_locale, locale_source)
               VALUES ({tenant}, {id}, {session.Id}, {session.ContactId}, {session.ChannelInstanceId.Value}, {now}, {idioma}, {origem})
               ON CONFLICT (tenant_id, session_id, channel_instance_id) DO NOTHING",
            cancellationToken);

        return await _db.Conversations.AsNoTracking().SingleAsync(c => c.SessionId == session.Id && c.ChannelInstanceId == session.ChannelInstanceId, cancellationToken);
    }

    public Task<Conversation?> FindConversationAsync(Guid conversationId, CancellationToken cancellationToken) =>
        _db.Conversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken);

    public Task<Contact?> FindContactAsync(Guid contactId, CancellationToken cancellationToken) =>
        _db.Contacts.FirstOrDefaultAsync(c => c.Id == contactId, cancellationToken);

    public Task<MessageTranslation?> FindTranslationAsync(Guid messageId, Locale targetLocale, CancellationToken cancellationToken) =>
        _db.Translations.AsNoTracking().FirstOrDefaultAsync(t => t.MessageId == messageId && t.TargetLocale == targetLocale, cancellationToken);

    public Task<Message?> FindMessageByProviderEventAsync(ChannelInstanceId channelInstanceId, string providerEventId, CancellationToken cancellationToken) =>
        _db.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.ChannelInstanceId == channelInstanceId && m.ProviderEventId == providerEventId, cancellationToken);

    public Task<Message?> FindReplyByTurnAsync(Guid sessionId, Guid turnId, CancellationToken cancellationToken) =>
        _db.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.SessionId == sessionId && m.TurnId == turnId, cancellationToken);

    public Task<Message?> FindResendByRequestAsync(Guid requestId, CancellationToken cancellationToken) =>
        _db.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.ResendRequestId == requestId, cancellationToken);

    public Task<bool> HasActiveResendAsync(Guid originalMessageId, CancellationToken cancellationToken) =>
        _db.Messages.AsNoTracking().AnyAsync(
            m => m.ResendOfMessageId == originalMessageId
                && (m.DeliveryState == DeliveryState.Requested
                    || m.DeliveryState == DeliveryState.Queued
                    || m.DeliveryState == DeliveryState.Sending
                    || m.DeliveryState == DeliveryState.SendUnknown),
            cancellationToken);

    public async Task<IReadOnlyList<Message>> RecentMessagesAsync(Guid sessionId, int take, CancellationToken cancellationToken)
    {
        var recentes = await _db.Messages.AsNoTracking()
            .Where(m => m.SessionId == sessionId)
            .OrderByDescending(m => m.OccurredAt).ThenByDescending(m => m.MessageSeq)
            .Take(take)
            .ToListAsync(cancellationToken);

        recentes.Reverse();
        return recentes;
    }

    public Task<Message?> LastOutboundAsync(Guid sessionId, CancellationToken cancellationToken) =>
        _db.Messages.AsNoTracking()
            .Where(m => m.SessionId == sessionId && m.Direction == MessageDirection.Outbound)
            .OrderByDescending(m => m.MessageSeq)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<HandlingTransition?> FindTransitionAsync(Guid sessionId, Guid transitionId, CancellationToken cancellationToken) =>
        _db.Transitions.AsNoTracking().FirstOrDefaultAsync(t => t.SessionId == sessionId && t.TransitionId == transitionId, cancellationToken);

    public void Add(Message message) => _db.Messages.Add(message);

    public Task<SessionTransfer?> FindTransferAsync(Guid sessionId, Guid transferId, CancellationToken cancellationToken) =>
        _db.Transfers.AsNoTracking().FirstOrDefaultAsync(t => t.SessionId == sessionId && t.TransferId == transferId, cancellationToken);

    public void Add(HandlingTransition transition) => _db.Transitions.Add(transition);

    public void Add(SessionTransfer transfer) => _db.Transfers.Add(transfer);

    public void Add(MessageTranslation translation) => _db.Translations.Add(translation);

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            await _transacao.CommitAsync(cancellationToken);
            _confirmada = true;
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex, MessageConfiguration.ProviderEventIndex))
        {
            throw new DuplicateProviderEventException();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_confirmada)
        {
            // Sem commit, o descarte desfaz a transação e libera a trava da sessão.
            await _transacao.RollbackAsync();
        }

        await _transacao.DisposeAsync();
    }
}
