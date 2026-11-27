using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Application;
using IaBimeta.Tenancy.Contracts;
using IaBimeta.Tenancy.Domain;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.Tenancy.Infrastructure;

internal sealed class TenantRepository : ITenantRepository
{
    private readonly TenancyDbContext _db;

    public TenantRepository(TenancyDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(Tenant tenant, CancellationToken cancellationToken)
    {
        _db.Tenants.Add(tenant);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex, "ix_tenants_slug"))
        {
            _db.Entry(tenant).State = EntityState.Detached;
            throw new DuplicateTenantSlugException(tenant.Slug);
        }
    }

    public Task<Tenant?> FindBySlugAsync(string slug, CancellationToken cancellationToken) =>
        _db.Tenants.FirstOrDefaultAsync(t => t.Slug == slug, cancellationToken);

    public Task<Tenant?> FindByIdAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        _db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);

    // Projeção SEM rastreamento: consulta a linha de verdade a cada chamada, mesmo que o contexto já rastreie a entidade (P1-02). Só este caminho muda; os repositórios administrativos seguem rastreando.
    public Task<TenantOperationSnapshot?> FindOperationStateAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        _db.Tenants.AsNoTracking().Where(t => t.Id == tenantId)
            .Select(t => new TenantOperationSnapshot(t.Status, t.ReactivatedAt))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<bool> TryCompleteProvisioningAsync(TenantId tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var alteradas = await _db.Tenants
            .Where(t => t.Id == tenantId && t.Status == TenantStatus.Provisioning && t.Kind == TenantKind.Commercial)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TenantStatus.Active).SetProperty(t => t.UpdatedAt, now), cancellationToken);

        // Uma entidade rastreada neste contexto ficou desatualizada com o UPDATE direto (ou já estava desde antes): relê do banco para quem a usar depois.
        var rastreada = _db.ChangeTracker.Entries<Tenant>().FirstOrDefault(e => e.Entity.Id == tenantId);
        if (rastreada is not null)
        {
            await rastreada.ReloadAsync(cancellationToken);
        }

        return alteradas > 0;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => _db.SaveChangesAsync(cancellationToken);

    public async Task<(IReadOnlyList<Tenant> Items, int Total)> ListCommercialAsync(string? search, TenantStatus? status, int skip, int take, CancellationToken cancellationToken)
    {
        var consulta = _db.Tenants.AsNoTracking().Where(t => t.Kind == TenantKind.Commercial);
        if (status is { } situacao)
        {
            consulta = consulta.Where(t => t.Status == situacao);
        }

        var termo = (search ?? string.Empty).Trim().ToLowerInvariant();
        if (termo.Length > 0)
        {
            var digitos = new string(termo.Where(char.IsDigit).ToArray());
            consulta = consulta.Where(t =>
                t.Slug.Contains(termo)
                || t.Name.ToLower().Contains(termo)
                || (t.LegalName != null && t.LegalName.ToLower().Contains(termo))
                || (digitos.Length >= 3 && t.Document != null && t.Document.Contains(digitos)));
        }

        var total = await consulta.CountAsync(cancellationToken);
        var itens = await consulta.OrderByDescending(t => t.CreatedAt).ThenBy(t => t.Slug).Skip(skip).Take(take).ToListAsync(cancellationToken);
        return (itens, total);
    }

    public async Task<IReadOnlyList<Tenant>> ListAllCommercialAsync(CancellationToken cancellationToken) =>
        await _db.Tenants.AsNoTracking().Where(t => t.Kind == TenantKind.Commercial).ToListAsync(cancellationToken);

    public Task<Tenant?> FindPlatformAsync(CancellationToken cancellationToken) =>
        _db.Tenants.FirstOrDefaultAsync(t => t.Kind == TenantKind.Platform, cancellationToken);

    public async Task SaveStatusChangeAsync(TenantStatusChange change, CancellationToken cancellationToken)
    {
        _db.StatusChanges.Add(change);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TenantStatusChange>> ListStatusChangesAsync(TenantId tenantId, int take, CancellationToken cancellationToken) =>
        await _db.StatusChanges.AsNoTracking().Where(c => c.TenantId == tenantId).OrderByDescending(c => c.ChangedAt).Take(take).ToListAsync(cancellationToken);
}
