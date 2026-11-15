using IaBimeta.Messaging;
using IaBimeta.SharedKernel;
using IaBimeta.Workflows.Core.Domain;
using IaBimeta.Workflows.Infrastructure.Configurations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Npgsql;

namespace IaBimeta.Workflows.Infrastructure;

/// <summary>
/// DbContext do módulo Workflows (esquema <c>workflows</c>). Mesmo isolamento por tenant dos demais módulos: filtro global e verificação no <c>SaveChanges</c>. Não há chave estrangeira para outros esquemas (DAD-2).
/// </summary>
public sealed class WorkflowsDbContext : DbContext
{
    public const string Schema = "workflows";

    private readonly IRequestContextAccessor _contexto;

    public WorkflowsDbContext(DbContextOptions<WorkflowsDbContext> options, IRequestContextAccessor contexto)
        : base(options)
    {
        _contexto = contexto;
    }

    public DbSet<WorkflowVersion> Versions => Set<WorkflowVersion>();

    public DbSet<FlowActivation> Activations => Set<FlowActivation>();

    public DbSet<RoutingDecision> Decisions => Set<RoutingDecision>();

    public DbSet<FlowInstance> Instances => Set<FlowInstance>();

    public DbSet<FlowEffectRecord> Effects => Set<FlowEffectRecord>();

    public DbSet<FlowStepRecord> Steps => Set<FlowStepRecord>();

    // O EF reavalia esta propriedade a cada execução de consulta (membro do DbContext no filtro global).
    internal TenantId TenantAtual => _contexto.GetRequired().TenantId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfiguration(new WorkflowVersionConfiguration());
        modelBuilder.Entity<WorkflowVersion>().HasQueryFilter(e => e.TenantId == TenantAtual);
        modelBuilder.ApplyConfiguration(new FlowActivationConfiguration());
        modelBuilder.ApplyConfiguration(new RoutingDecisionConfiguration());
        modelBuilder.ApplyConfiguration(new FlowInstanceConfiguration());
        modelBuilder.ApplyConfiguration(new FlowEffectConfiguration());
        modelBuilder.ApplyConfiguration(new FlowStepConfiguration());
        modelBuilder.Entity<FlowActivation>().HasQueryFilter(e => e.TenantId == TenantAtual);
        modelBuilder.Entity<RoutingDecision>().HasQueryFilter(e => e.TenantId == TenantAtual);
        modelBuilder.Entity<FlowInstance>().HasQueryFilter(e => e.TenantId == TenantAtual);
        modelBuilder.Entity<FlowEffectRecord>().HasQueryFilter(e => e.TenantId == TenantAtual);
        modelBuilder.Entity<FlowStepRecord>().HasQueryFilter(e => e.TenantId == TenantAtual);

        // Inbox do consumidor (BOUNDARIES.md §12.1): a idempotência por EventId do que este módulo consome do Conversations.
        modelBuilder.ConfigureInbox();
        modelBuilder.ConfigureOutbox();
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

/// <summary>Tentativa de gravar, alterar ou excluir uma entidade de um tenant diferente do tenant do contexto da operação. É bloqueada antes de chegar ao banco.</summary>
public sealed class CrossTenantAccessException : InvalidOperationException
{
    public CrossTenantAccessException(string message)
        : base(message)
    {
    }
}

internal sealed class TenantIdConverter : ValueConverter<TenantId, Guid>
{
    public TenantIdConverter()
        : base(tenantId => tenantId.Value, valor => new TenantId(valor))
    {
    }
}

internal static class PostgresErrors
{
    public static bool IsUniqueViolation(DbUpdateException exception, out string? constraint)
    {
        if (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            constraint = pg.ConstraintName;
            return true;
        }

        constraint = null;
        return false;
    }
}

/// <summary>A fábrica de tempo de design (dotnet ef): só para gerar migrations, sem abrir conexão.</summary>
public sealed class WorkflowsDbContextFactory : IDesignTimeDbContextFactory<WorkflowsDbContext>
{
    public WorkflowsDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<WorkflowsDbContext>()
            .UseNpgsql("Host=localhost;Database=design_time_only", npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", WorkflowsDbContext.Schema))
            .Options,
        new RequestContextHolder());
}
