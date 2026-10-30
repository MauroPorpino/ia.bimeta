using IaBimeta.ContactCenter.Domain;
using IaBimeta.ContactCenter.Infrastructure.Configurations;
using IaBimeta.Messaging;
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

    /// <summary>
    /// A sequência dos tokens de cerca (<c>fencing token</c>) de quem executa operações pendentes no Conversations: cada execução pega o
    /// próximo valor DEPOIS de obter a trava do item, então um executor que assume depois sempre leva um token maior que o do anterior,
    /// e o Conversations recusa o menor. Global (não por tenant), para valer também entre itens da mesma sessão em momentos diferentes.
    /// </summary>
    public const string ExecutorFenceSequence = "executor_fence_seq";

    private readonly IRequestContextAccessor _contexto;

    public ContactCenterDbContext(DbContextOptions<ContactCenterDbContext> options, IRequestContextAccessor contexto)
        : base(options)
    {
        _contexto = contexto;
    }

    public DbSet<ServiceQueue> Queues => Set<ServiceQueue>();

    public DbSet<QueueMembership> Memberships => Set<QueueMembership>();

    public DbSet<QueueItem> Items => Set<QueueItem>();

    public DbSet<QueueItemHistory> History => Set<QueueItemHistory>();

    public DbSet<QueueOperation> Operations => Set<QueueOperation>();

    public DbSet<PendingAudit> AuditOutbox => Set<PendingAudit>();

    public DbSet<RoutingRule> RoutingRules => Set<RoutingRule>();

    public DbSet<QueueScheduleWindow> ScheduleWindows => Set<QueueScheduleWindow>();

    public DbSet<QueueClosure> ScheduleClosures => Set<QueueClosure>();

    public DbSet<AgentPresence> Presences => Set<AgentPresence>();

    // O EF reavalia esta propriedade a cada execução de consulta (membro do DbContext no filtro global).
    private TenantId TenantAtual => _contexto.GetRequired().TenantId;

    /// <summary>O tenant do contexto confiável, para o SQL escrito à mão (que não passa pelo filtro global): falha fechada sem contexto.</summary>
    internal Guid CurrentTenantId => TenantAtual.Value;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.HasSequence<long>(ExecutorFenceSequence, Schema).StartsAt(1).IncrementsBy(1);

        modelBuilder.ApplyConfiguration(new ServiceQueueConfiguration());
        modelBuilder.ApplyConfiguration(new QueueMembershipConfiguration());
        modelBuilder.ApplyConfiguration(new QueueItemConfiguration());
        modelBuilder.ApplyConfiguration(new QueueItemHistoryConfiguration());
        modelBuilder.ApplyConfiguration(new QueueOperationConfiguration());
        modelBuilder.ApplyConfiguration(new PendingAuditConfiguration());
        modelBuilder.ApplyConfiguration(new RoutingRuleConfiguration());
        modelBuilder.ApplyConfiguration(new QueueScheduleWindowConfiguration());
        modelBuilder.ApplyConfiguration(new QueueClosureConfiguration());
        modelBuilder.ApplyConfiguration(new AgentPresenceConfiguration());

        // Inbox do consumidor (BOUNDARIES.md §12.1): a idempotência por EventId do que este módulo consome do Conversations.
        modelBuilder.ConfigureInbox();

        modelBuilder.Entity<ServiceQueue>().HasQueryFilter(e => e.TenantId == TenantAtual);
        modelBuilder.Entity<QueueMembership>().HasQueryFilter(e => e.TenantId == TenantAtual);
        modelBuilder.Entity<QueueItem>().HasQueryFilter(e => e.TenantId == TenantAtual);
        modelBuilder.Entity<QueueItemHistory>().HasQueryFilter(e => e.TenantId == TenantAtual);
        modelBuilder.Entity<QueueOperation>().HasQueryFilter(e => e.TenantId == TenantAtual);
        modelBuilder.Entity<PendingAudit>().HasQueryFilter(e => e.TenantId == TenantAtual);
        modelBuilder.Entity<RoutingRule>().HasQueryFilter(e => e.TenantId == TenantAtual);
        modelBuilder.Entity<QueueScheduleWindow>().HasQueryFilter(e => e.TenantId == TenantAtual);
        modelBuilder.Entity<QueueClosure>().HasQueryFilter(e => e.TenantId == TenantAtual);
        modelBuilder.Entity<AgentPresence>().HasQueryFilter(e => e.TenantId == TenantAtual);
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
