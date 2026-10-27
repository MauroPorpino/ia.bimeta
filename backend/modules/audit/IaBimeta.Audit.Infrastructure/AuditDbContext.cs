using IaBimeta.Audit.Domain;
using IaBimeta.Audit.Infrastructure.Configurations;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.Audit.Infrastructure;

/// <summary>
/// DbContext do módulo Audit (esquema <c>audit</c>). Mesmo isolamento por tenant do Tenancy: filtro global e
/// verificação no <c>SaveChanges</c>, ambos no aplicativo. Além disso, o próprio banco recusa alterar ou excluir um
/// evento já gravado (gatilhos criados pela migration): a trilha só recebe inclusões.
/// </summary>
public sealed class AuditDbContext : DbContext
{
    public const string Schema = "audit";

    private readonly IRequestContextAccessor _contexto;
    private TenantId? _tenantDaAutenticacao;

    public AuditDbContext(DbContextOptions<AuditDbContext> options, IRequestContextAccessor contexto)
        : base(options)
    {
        _contexto = contexto;
    }

    public DbSet<AuditEvent> Events => Set<AuditEvent>();

    // O EF reavalia esta propriedade a cada execução de consulta (membro do DbContext no filtro global).
    private TenantId TenantAtual => _tenantDaAutenticacao ?? _contexto.GetRequired().TenantId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.ApplyConfiguration(new AuditEventConfiguration());

        modelBuilder.Entity<AuditEvent>().HasQueryFilter(e => e.TenantId == TenantAtual);
    }

    /// <summary>
    /// Fixa o tenant para um evento anterior ao contexto (a autenticação). Só vale para o tenant informado e nunca
    /// substitui um contexto já estabelecido de outro tenant.
    /// </summary>
    internal IDisposable UseAuthenticationTenant(TenantId tenantId)
    {
        var atual = _contexto.Current;
        if (atual is not null && atual.TenantId != tenantId)
        {
            throw new CrossTenantAccessException("Operação bloqueada: o tenant da autenticação difere do tenant do contexto estabelecido.");
        }

        _tenantDaAutenticacao = tenantId;
        return new EscopoDaAutenticacao(this);
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

    private sealed class EscopoDaAutenticacao : IDisposable
    {
        private readonly AuditDbContext _db;

        public EscopoDaAutenticacao(AuditDbContext db)
        {
            _db = db;
        }

        public void Dispose() => _db._tenantDaAutenticacao = null;
    }
}
