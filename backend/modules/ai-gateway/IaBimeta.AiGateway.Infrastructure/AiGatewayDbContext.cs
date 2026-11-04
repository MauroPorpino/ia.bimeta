using IaBimeta.AiGateway.Domain;
using IaBimeta.AiGateway.Infrastructure.Configurations;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.AiGateway.Infrastructure;

/// <summary>
/// DbContext do módulo AiGateway (esquema <c>ai_gateway</c>). Mesmo isolamento por tenant dos demais módulos: filtro global e verificação no <c>SaveChanges</c> (RLS continua
/// proposta a validar, TEN-3). Não há chave estrangeira para outros esquemas (DAD-2). O ledger (<c>ai_requests</c> e <c>usage_windows</c>) é gravado por SQL atômico no
/// <c>PostgresAiUsageLedger</c>; as entidades dele servem à leitura e ao modelo.
/// </summary>
public sealed class AiGatewayDbContext : DbContext
{
    public const string Schema = "ai_gateway";

    private readonly IRequestContextAccessor _contexto;

    public AiGatewayDbContext(DbContextOptions<AiGatewayDbContext> options, IRequestContextAccessor contexto)
        : base(options)
    {
        _contexto = contexto;
    }

    public DbSet<AiTenantSettings> Settings => Set<AiTenantSettings>();

    public DbSet<StoredAiCredential> Credentials => Set<StoredAiCredential>();

    public DbSet<AiRequest> Requests => Set<AiRequest>();

    public DbSet<AiUsageWindow> Windows => Set<AiUsageWindow>();

    // O EF reavalia esta propriedade a cada execução de consulta (membro do DbContext no filtro global).
    private TenantId TenantAtual => _contexto.GetRequired().TenantId;

    /// <summary>O tenant do contexto confiável, para o SQL cru do ledger (que não passa pelo filtro global): quem o usa confere que o pedido é DESTE tenant.</summary>
    internal TenantId CurrentTenant => TenantAtual;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.ApplyConfiguration(new AiTenantSettingsConfiguration());
        modelBuilder.ApplyConfiguration(new StoredAiCredentialConfiguration());
        modelBuilder.ApplyConfiguration(new AiRequestConfiguration());
        modelBuilder.ApplyConfiguration(new AiUsageWindowConfiguration());

        modelBuilder.Entity<AiTenantSettings>().HasQueryFilter(e => e.TenantId == TenantAtual);
        modelBuilder.Entity<StoredAiCredential>().HasQueryFilter(e => e.TenantId == TenantAtual);
        modelBuilder.Entity<AiRequest>().HasQueryFilter(e => e.TenantId == TenantAtual);
        modelBuilder.Entity<AiUsageWindow>().HasQueryFilter(e => e.TenantId == TenantAtual);
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
