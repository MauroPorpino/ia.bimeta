using IaBimeta.Channels.Application;
using IaBimeta.Channels.Contracts;
using IaBimeta.Channels.Domain;
using IaBimeta.Channels.Infrastructure.Configurations;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.Channels.Infrastructure;

/// <summary>
/// Toda consulta passa pelo filtro global do <see cref="ChannelsDbContext"/>: só enxerga o tenant do contexto e, sem contexto,
/// falha fechada (TEN-2 e TEN-3). A exceção é a resolução da rota de entrada, que roda antes de o tenant ser conhecido.
/// </summary>
internal sealed class ChannelRepository : IChannelRepository
{
    private readonly ChannelsDbContext _db;

    public ChannelRepository(ChannelsDbContext db)
    {
        _db = db;
    }

    public async Task AddAccountAsync(ChannelAccount account, CancellationToken cancellationToken)
    {
        _db.Accounts.Add(account);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex, ChannelAccountConfiguration.UniqueProviderAccountIndex))
        {
            _db.Entry(account).State = EntityState.Detached;
            throw new ChannelIdentifierInUseException("da conta");
        }
    }

    public Task<ChannelAccount?> FindAccountAsync(Guid id, CancellationToken cancellationToken) =>
        _db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

    public async Task<IReadOnlyList<ChannelAccount>> ListAccountsAsync(IReadOnlyCollection<Guid>? organizationIds, CancellationToken cancellationToken)
    {
        var consulta = _db.Accounts.AsNoTracking();
        if (organizationIds is not null)
        {
            consulta = consulta.Where(a => organizationIds.Contains(a.OrganizationId));
        }

        return await consulta.OrderBy(a => a.Name).ThenBy(a => a.Id).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ChannelSecret>> ListSecretsAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken) =>
        accountIds.Count == 0
            ? Array.Empty<ChannelSecret>()
            : await _db.Secrets.AsNoTracking().Where(s => accountIds.Contains(s.AccountId)).ToListAsync(cancellationToken);

    public Task<ChannelSecret?> FindSecretAsync(Guid accountId, ChannelSecretKind kind, CancellationToken cancellationToken) =>
        _db.Secrets.FirstOrDefaultAsync(s => s.AccountId == accountId && s.Kind == kind, cancellationToken);

    public async Task AddSecretAsync(ChannelSecret secret, CancellationToken cancellationToken)
    {
        _db.Secrets.Add(secret);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex, ChannelSecretConfiguration.UniqueKindIndex))
        {
            // Outra requisição definiu o mesmo papel primeiro: quem repetir gira o valor existente.
            _db.Entry(secret).State = EntityState.Detached;
            throw new ChannelsConflictException();
        }
    }

    public async Task AddInstanceAsync(ChannelInstance instance, CancellationToken cancellationToken)
    {
        var rota = ChannelRoute.For(instance);
        _db.Instances.Add(instance);
        _db.Routes.Add(rota);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (
            PostgresErrors.IsUniqueViolation(ex, ChannelInstanceConfiguration.UniquePhoneNumberIndex)
            || PostgresErrors.IsUniqueViolation(ex, ChannelRouteConfiguration.UniqueRouteIndex))
        {
            _db.Entry(rota).State = EntityState.Detached;
            _db.Entry(instance).State = EntityState.Detached;
            throw new ChannelIdentifierInUseException("do número");
        }
    }

    public Task<ChannelInstance?> FindInstanceAsync(ChannelInstanceId id, CancellationToken cancellationToken) =>
        _db.Instances.Include(i => i.AuthorizedUnits).FirstOrDefaultAsync(i => i.Id == id, cancellationToken);

    public Task<ChannelInstance?> FindInstanceReadOnlyAsync(ChannelInstanceId id, CancellationToken cancellationToken) =>
        _db.Instances.AsNoTracking().Include(i => i.AuthorizedUnits).FirstOrDefaultAsync(i => i.Id == id, cancellationToken);

    public async Task<IReadOnlyList<ChannelInstance>> ListInstancesAsync(IReadOnlyCollection<Guid>? organizationIds, CancellationToken cancellationToken)
    {
        var consulta = _db.Instances.AsNoTracking().Include(i => i.AuthorizedUnits).AsQueryable();
        if (organizationIds is not null)
        {
            consulta = consulta.Where(i => organizationIds.Contains(i.OrganizationId));
        }

        return await consulta.OrderBy(i => i.Name).ThenBy(i => i.Id).ToListAsync(cancellationToken);
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Descarta o que foi rastreado: o chamador relê o recurso antes de tentar de novo.
            _db.ChangeTracker.Clear();
            throw new ChannelsConflictException();
        }
    }

    public async Task<ChannelRouteResolution?> ResolveRouteAsync(ChannelProvider provider, string externalId, CancellationToken cancellationToken)
    {
        // Sem contexto de tenant: IgnoreQueryFilters vale para a consulta toda e o filtro (que exigiria o contexto) nem é avaliado.
        // A projeção devolve só as chaves de roteamento e a situação do número.
        var linha = await (
            from rota in _db.Routes.IgnoreQueryFilters().AsNoTracking()
            join instancia in _db.Instances.IgnoreQueryFilters()
                on new { rota.TenantId, Id = rota.ChannelInstanceId } equals new { instancia.TenantId, instancia.Id }
            where rota.Provider == provider && rota.ExternalId == externalId
            select new { rota.TenantId, rota.ChannelInstanceId, instancia.Status })
            .FirstOrDefaultAsync(cancellationToken);

        return linha is null ? null : new ChannelRouteResolution(linha.ChannelInstanceId, linha.TenantId, linha.Status);
    }
}
