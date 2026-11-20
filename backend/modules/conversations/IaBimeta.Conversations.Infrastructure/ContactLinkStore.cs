using IaBimeta.Conversations.Application;
using IaBimeta.Conversations.Domain;
using IaBimeta.Conversations.Infrastructure.Configurations;
using IaBimeta.Messaging;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace IaBimeta.Conversations.Infrastructure;

/// <summary>
/// A persistência do vínculo (ADR 0041, E2.2). Cada unidade de trabalho é UMA transação: o vínculo e o pedido de auditoria (outbox do módulo) entram no mesmo <c>SaveChanges</c>, e ou os dois são confirmados ou nenhum.
/// As operações do mesmo cliente e do mesmo contato se serializam por trava consultiva da transação (na mesma ordem sempre, sem deadlock); o índice único parcial do banco é a última barreira contra o vínculo ativo
/// duplicado, e perder essa corrida devolve <c>false</c> (a operação recomeça e agora é idempotente).
/// </summary>
internal sealed class ContactLinkStore : IContactLinkStore
{
    private readonly ConversationsDbContext _db;
    private readonly TimeProvider _clock;
    private readonly IOutboxWriter _outbox;

    public ContactLinkStore(ConversationsDbContext db, TimeProvider clock, IOutboxWriter outbox)
    {
        _db = db;
        _clock = clock;
        _outbox = outbox;
    }

    public async Task<IContactLinkUnitOfWork> BeginAsync(CancellationToken cancellationToken)
    {
        // Falha fechada: sem tenant no contexto o filtro global do DbContext já recusa; aqui só abrimos a transação.
        var transacao = await _db.Database.BeginTransactionAsync(cancellationToken);
        return new ContactLinkUnitOfWork(_db, transacao, _clock, _outbox);
    }
}

internal sealed class ContactLinkUnitOfWork : IContactLinkUnitOfWork
{
    private readonly ConversationsDbContext _db;
    private readonly IDbContextTransaction _transacao;
    private readonly TimeProvider _clock;
    private readonly IOutboxWriter _outbox;
    private bool _confirmada;

    public ContactLinkUnitOfWork(ConversationsDbContext db, IDbContextTransaction transacao, TimeProvider clock, IOutboxWriter outbox)
    {
        _db = db;
        _transacao = transacao;
        _clock = clock;
        _outbox = outbox;
    }

    public async Task<IReadOnlyList<Guid>> FindContactIdsByPhoneAsync(string phoneDigits, Guid organizationId, CancellationToken cancellationToken)
    {
        var comMais = "+" + phoneDigits;

        // O contato do telefone (a referência do provedor é o telefone em dígitos, o formato do wa_id) QUE TEM SESSÃO na organização: um contato que só existe em outra organização nunca é resolvido por esta conexão.
        return await _db.Contacts.AsNoTracking()
            .Where(c => (c.ExternalRef == phoneDigits || c.ExternalRef == comMais) && _db.Sessions.Any(s => s.ContactId == c.Id && s.OrganizationId == organizationId))
            .Select(c => c.Id)
            .OrderBy(id => id)
            .ToListAsync(cancellationToken);
    }

    public Task<LinkSessionInfo?> FindSessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
        _db.Sessions.AsNoTracking()
            .Where(s => s.Id == sessionId)
            .Select(s => new LinkSessionInfo(s.OrganizationId, s.ContactId))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task LockAsync(Guid connectionId, string customerRef, IReadOnlyCollection<Guid> contactIds, CancellationToken cancellationToken)
    {
        var tenant = _db.CurrentTenantValue;

        // Primeiro o cliente, depois os contatos em ordem crescente: a mesma ordem em toda operação, então não há ciclo de espera (deadlock).
        var chaveDoCliente = $"contact-link:{tenant:N}:{connectionId:N}:customer:{customerRef}";
        await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({chaveDoCliente}, 0))", cancellationToken);
        foreach (var contato in contactIds.Distinct().Order())
        {
            var chaveDoContato = $"contact-link:{tenant:N}:{connectionId:N}:contact:{contato:N}";
            await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({chaveDoContato}, 0))", cancellationToken);
        }
    }

    public async Task<IReadOnlyList<ContactExternalLink>> ListActiveByCustomerAsync(Guid connectionId, string customerRef, CancellationToken cancellationToken) =>
        await _db.ContactLinks.Where(l => l.ConnectionId == connectionId && l.CustomerRef == customerRef && l.UnlinkedAt == null).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ContactExternalLink>> ListActiveByContactsAsync(Guid connectionId, IReadOnlyCollection<Guid> contactIds, CancellationToken cancellationToken) =>
        contactIds.Count == 0
            ? Array.Empty<ContactExternalLink>()
            : await _db.ContactLinks.Where(l => l.ConnectionId == connectionId && contactIds.Contains(l.ContactId) && l.UnlinkedAt == null).ToListAsync(cancellationToken);

    public void Add(ContactExternalLink link) => _db.ContactLinks.Add(link);

    public async Task<bool> CommitAsync(IReadOnlyCollection<EventEnvelope> events, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);

        try
        {
            // A auditoria entra no MESMO SaveChanges do efeito (outbox do módulo): se a transação é desfeita, ela some junto.
            if (events.Count > 0)
            {
                _outbox.Stage(_db, events, new TenantId(_db.CurrentTenantValue), _clock.GetUtcNow());
            }

            await _db.SaveChangesAsync(cancellationToken);
            await _transacao.CommitAsync(cancellationToken);
            _confirmada = true;
            return true;
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex, ContactExternalLinkConfiguration.ActiveUniqueIndex))
        {
            // Outro vínculo ativo igual entrou antes (corrida): descarta tudo (nada fica rastreado) e a operação recomeça.
            _db.ChangeTracker.Clear();
            return false;
        }
        catch
        {
            // Todo caminho de falha desanexa o que foi adicionado: o contexto desta requisição não pode guardar um vínculo que não foi gravado.
            _db.ChangeTracker.Clear();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_confirmada)
        {
            _db.ChangeTracker.Clear();
            await _transacao.RollbackAsync();
        }

        await _transacao.DisposeAsync();
    }
}
