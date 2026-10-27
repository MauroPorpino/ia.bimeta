using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Domain;
using IaBimeta.Tenancy.Infrastructure.Configurations;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.Tenancy.Infrastructure;

/// <summary>
/// DbContext do módulo Tenancy (esquema <c>tenancy</c>). Isolamento por tenant em duas camadas, ambas no aplicativo
/// (RLS no banco continua proposta a validar, BOUNDARIES.md TEN-3):
/// 1. filtro global: toda consulta de uma entidade <see cref="ITenantOwned"/> só enxerga o tenant do contexto, e sem
///    contexto falha fechada;
/// 2. verificação no <c>SaveChanges</c>: não grava, altera nem exclui entidade de outro tenant, mesmo que tenha sido
///    carregada com <c>IgnoreQueryFilters()</c>.
/// </summary>
public sealed class TenancyDbContext : DbContext
{
    public const string Schema = "tenancy";

    private readonly IRequestContextAccessor _contexto;

    public TenancyDbContext(DbContextOptions<TenancyDbContext> options, IRequestContextAccessor contexto)
        : base(options)
    {
        _contexto = contexto;
    }

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<Organization> Organizations => Set<Organization>();

    public DbSet<BusinessUnit> BusinessUnits => Set<BusinessUnit>();

    public DbSet<Department> Departments => Set<Department>();

    // O EF reavalia esta propriedade a cada execução de consulta (membro do DbContext no filtro global).
    private TenantId TenantAtual => _contexto.GetRequired().TenantId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.ApplyConfiguration(new TenantConfiguration());
        modelBuilder.ApplyConfiguration(new OrganizationConfiguration());
        modelBuilder.ApplyConfiguration(new BusinessUnitConfiguration());
        modelBuilder.ApplyConfiguration(new DepartmentConfiguration());

        modelBuilder.Entity<Organization>().HasQueryFilter(o => o.TenantId == TenantAtual);
        modelBuilder.Entity<BusinessUnit>().HasQueryFilter(u => u.TenantId == TenantAtual);
        modelBuilder.Entity<Department>().HasQueryFilter(d => d.TenantId == TenantAtual);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        VerificarIsolamentoDoTenant();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        VerificarIsolamentoDoTenant();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void VerificarIsolamentoDoTenant()
    {
        foreach (var entrada in ChangeTracker.Entries<ITenantOwned>())
        {
            if (entrada.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            var tenantAtual = _contexto.GetRequired().TenantId;

            if (entrada.Entity.TenantId != tenantAtual)
            {
                throw new CrossTenantAccessException(
                    $"Operação bloqueada: {entrada.Metadata.ClrType.Name} de outro tenant não pode ser gravada, alterada nem excluída neste contexto.");
            }
        }
    }
}
