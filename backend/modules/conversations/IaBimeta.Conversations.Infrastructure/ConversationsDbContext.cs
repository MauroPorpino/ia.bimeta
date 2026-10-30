using IaBimeta.Conversations.Domain;
using IaBimeta.Conversations.Infrastructure.Configurations;
using IaBimeta.Messaging;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.Conversations.Infrastructure;

/// <summary>
/// DbContext do módulo Conversations (esquema <c>conversations</c>). Mesmo isolamento por tenant do Tenancy: filtro global e
/// verificação no <c>SaveChanges</c> (RLS continua proposta a validar, TEN-3). Não há chave estrangeira para outros esquemas
/// (DAD-2): organização, unidade e canal são identificadores opacos, validados pelos casos de uso.
/// </summary>
public sealed class ConversationsDbContext : DbContext
{
    public const string Schema = "conversations";

    private readonly IRequestContextAccessor _contexto;

    public ConversationsDbContext(DbContextOptions<ConversationsDbContext> options, IRequestContextAccessor contexto)
        : base(options)
    {
        _contexto = contexto;
    }

    public DbSet<Contact> Contacts => Set<Contact>();

    public DbSet<ServiceSession> Sessions => Set<ServiceSession>();

    public DbSet<Conversation> Conversations => Set<Conversation>();

    public DbSet<Message> Messages => Set<Message>();

    public DbSet<HandlingTransition> Transitions => Set<HandlingTransition>();

    public DbSet<MessageTranslation> Translations => Set<MessageTranslation>();

    public DbSet<SessionTransfer> Transfers => Set<SessionTransfer>();

    /// <summary>O tenant do contexto (falha fechada sem contexto). Também usado nas instruções SQL diretas da unidade de trabalho.</summary>
    internal Guid CurrentTenantValue => TenantAtual.Value;

    // O EF reavalia esta propriedade a cada execução de consulta (membro do DbContext no filtro global).
    private TenantId TenantAtual => _contexto.GetRequired().TenantId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.ApplyConfiguration(new ContactConfiguration());
        modelBuilder.ApplyConfiguration(new ServiceSessionConfiguration());
        modelBuilder.ApplyConfiguration(new ConversationConfiguration());
        modelBuilder.ApplyConfiguration(new MessageConfiguration());
        modelBuilder.ApplyConfiguration(new HandlingTransitionConfiguration());
        modelBuilder.ApplyConfiguration(new MessageTranslationConfiguration());
        modelBuilder.ApplyConfiguration(new SessionTransferConfiguration());

        // Outbox de eventos duráveis do módulo (BOUNDARIES.md §12.1): gravado na mesma transação da mudança de estado.
        modelBuilder.ConfigureOutbox();

        modelBuilder.Entity<Contact>().HasQueryFilter(e => e.TenantId == TenantAtual);
        modelBuilder.Entity<ServiceSession>().HasQueryFilter(e => e.TenantId == TenantAtual);
        modelBuilder.Entity<Conversation>().HasQueryFilter(e => e.TenantId == TenantAtual);
        modelBuilder.Entity<Message>().HasQueryFilter(e => e.TenantId == TenantAtual);
        modelBuilder.Entity<HandlingTransition>().HasQueryFilter(e => e.TenantId == TenantAtual);
        modelBuilder.Entity<MessageTranslation>().HasQueryFilter(e => e.TenantId == TenantAtual);
        modelBuilder.Entity<SessionTransfer>().HasQueryFilter(e => e.TenantId == TenantAtual);
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
