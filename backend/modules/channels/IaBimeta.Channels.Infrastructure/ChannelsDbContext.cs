using IaBimeta.Channels.Domain;
using IaBimeta.Channels.Infrastructure.Configurations;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.Channels.Infrastructure;

/// <summary>
/// DbContext do módulo Channels (esquema <c>channels</c>). Mesmo isolamento por tenant dos demais módulos: filtro global e
/// verificação no <c>SaveChanges</c> (RLS continua proposta a validar, TEN-3). Não há chave estrangeira para outros esquemas
/// (DAD-2): organização, unidade e setor são identificadores opacos, validados pelos casos de uso.
/// <para>
/// A única exceção ao filtro por tenant é <see cref="Routes"/>: a rota de entrada é consultada antes de o tenant ser conhecido
/// (o webhook só traz o identificador do número no provedor). Ela só tem chaves de roteamento e só é lida pela consulta
/// dedicada do repositório; a escrita continua protegida pela verificação do <c>SaveChanges</c>.
/// </para>
/// </summary>
public sealed class ChannelsDbContext : DbContext
{
    public const string Schema = "channels";

    private readonly IRequestContextAccessor _contexto;

    public ChannelsDbContext(DbContextOptions<ChannelsDbContext> options, IRequestContextAccessor contexto)
        : base(options)
    {
        _contexto = contexto;
    }

    public DbSet<ChannelAccount> Accounts => Set<ChannelAccount>();

    public DbSet<ChannelInstance> Instances => Set<ChannelInstance>();

    public DbSet<ChannelInstanceUnit> InstanceUnits => Set<ChannelInstanceUnit>();

    /// <summary>Sem filtro de tenant, de propósito (ver o resumo da classe). Nunca leia por aqui em código que já tem tenant.</summary>
    public DbSet<ChannelRoute> Routes => Set<ChannelRoute>();

    public DbSet<ChannelSecret> Secrets => Set<ChannelSecret>();

    // O EF reavalia esta propriedade a cada execução de consulta (membro do DbContext no filtro global).
    private TenantId TenantAtual => _contexto.GetRequired().TenantId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.ApplyConfiguration(new ChannelAccountConfiguration());
        modelBuilder.ApplyConfiguration(new ChannelInstanceConfiguration());
        modelBuilder.ApplyConfiguration(new ChannelInstanceUnitConfiguration());
        modelBuilder.ApplyConfiguration(new ChannelRouteConfiguration());
        modelBuilder.ApplyConfiguration(new ChannelSecretConfiguration());

        modelBuilder.Entity<ChannelAccount>().HasQueryFilter(e => e.TenantId == TenantAtual);
        modelBuilder.Entity<ChannelInstance>().HasQueryFilter(e => e.TenantId == TenantAtual);
        modelBuilder.Entity<ChannelInstanceUnit>().HasQueryFilter(e => e.TenantId == TenantAtual);
        modelBuilder.Entity<ChannelSecret>().HasQueryFilter(e => e.TenantId == TenantAtual);
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
