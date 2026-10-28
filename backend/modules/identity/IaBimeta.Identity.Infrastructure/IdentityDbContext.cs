using IaBimeta.Identity.Domain;
using IaBimeta.Identity.Infrastructure.Configurations;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.Identity.Infrastructure;

/// <summary>
/// DbContext do módulo Identity (esquema <c>identity</c>). Mesmo isolamento por tenant do Tenancy: filtro global e
/// verificação no <c>SaveChanges</c>, ambos no aplicativo (RLS continua proposta a validar, TEN-3).
/// A autenticação acontece antes de existir contexto; para ela, o <see cref="AuthenticationStore"/> usa
/// <see cref="UseAuthenticationTenant"/>, um escopo interno ao módulo que vale para um tenant já resolvido por fonte
/// confiável e nunca para outro.
/// </summary>
public sealed class IdentityDbContext : DbContext
{
    public const string Schema = "identity";

    private readonly IRequestContextAccessor _contexto;
    private TenantId? _tenantDaAutenticacao;

    public IdentityDbContext(DbContextOptions<IdentityDbContext> options, IRequestContextAccessor contexto)
        : base(options)
    {
        _contexto = contexto;
    }

    public DbSet<User> Users => Set<User>();

    public DbSet<UserOrganizationGrant> Grants => Set<UserOrganizationGrant>();

    public DbSet<Session> Sessions => Set<Session>();

    public DbSet<LoginThrottle> LoginThrottles => Set<LoginThrottle>();

    // O EF reavalia esta propriedade a cada execução de consulta (membro do DbContext no filtro global).
    private TenantId TenantAtual => _tenantDaAutenticacao ?? _contexto.GetRequired().TenantId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.ApplyConfiguration(new UserConfiguration());
        modelBuilder.ApplyConfiguration(new UserOrganizationGrantConfiguration());
        modelBuilder.ApplyConfiguration(new SessionConfiguration());
        modelBuilder.ApplyConfiguration(new LoginThrottleConfiguration());

        modelBuilder.Entity<LoginThrottle>().HasQueryFilter(t => t.TenantId == TenantAtual);
        modelBuilder.Entity<User>().HasQueryFilter(u => u.TenantId == TenantAtual);
        modelBuilder.Entity<UserOrganizationGrant>().HasQueryFilter(g => g.TenantId == TenantAtual);
        modelBuilder.Entity<Session>().HasQueryFilter(s => s.TenantId == TenantAtual);
    }

    /// <summary>
    /// Fixa o tenant para uma gravação anterior ao contexto (a autenticação). Só vale para o tenant informado e nunca
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
        private readonly IdentityDbContext _db;

        public EscopoDaAutenticacao(IdentityDbContext db)
        {
            _db = db;
        }

        public void Dispose() => _db._tenantDaAutenticacao = null;
    }
}
