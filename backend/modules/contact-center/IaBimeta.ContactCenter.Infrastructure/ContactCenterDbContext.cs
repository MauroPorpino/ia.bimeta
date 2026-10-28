using IaBimeta.ContactCenter.Domain;
using IaBimeta.ContactCenter.Infrastructure.Configurations;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.ContactCenter.Infrastructure;

/// <summary>
/// DbContext do módulo ContactCenter (esquema <c>contact_center</c>). Mesmo isolamento por tenant dos demais módulos: filtro
/// global e verificação no <c>SaveChanges</c> (RLS continua proposta a validar, TEN-3). Não há chave estrangeira para outros
/// esquemas (DAD-2): organização, unidade, setor e usuário são identificadores opacos, validados pelos casos de uso.
/// </summary>
public sealed class ContactCenterDbContext : DbContext
{
    public const string Schema = "contact_center";

    private readonly IRequestContextAccessor _contexto;

    public ContactCenterDbContext(DbContextOptions<ContactCenterDbContext> options, IRequestContextAccessor contexto)
        : base(options)
    {
        _contexto = contexto;
    }

    public DbSet<ServiceQueue> Queues => Set<ServiceQueue>();

    public DbSet<QueueMembership> Memberships => Set<QueueMembership>();

    // O EF reavalia esta propriedade a cada execução de consulta (membro do DbContext no filtro global).
    private TenantId TenantAtual => _contexto.GetRequired().TenantId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.ApplyConfiguration(new ServiceQueueConfiguration());
        modelBuilder.ApplyConfiguration(new QueueMembershipConfiguration());

        modelBuilder.Entity<ServiceQueue>().HasQueryFilter(e => e.TenantId == TenantAtual);
        modelBuilder.Entity<QueueMembership>().HasQueryFilter(e => e.TenantId == TenantAtual);
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

            if (entrada.Entity.TenantId != TenantAtual)
            {
                throw new CrossTenantAccessException(
                    $"Operação bloqueada: {entrada.Metadata.ClrType.Name} de outro tenant não pode ser gravada, alterada nem excluída neste contexto.");
            }
        }
    }
}
