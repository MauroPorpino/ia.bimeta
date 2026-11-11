using IaBimeta.Agents.Domain;
using IaBimeta.Agents.Infrastructure.Configurations;
using IaBimeta.Messaging;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.Agents.Infrastructure;

/// <summary>
/// DbContext do módulo Agents (esquema <c>agents</c>). Mesmo isolamento por tenant dos demais módulos: filtro global e verificação no <c>SaveChanges</c> (RLS continua proposta a validar, TEN-3). Não há
/// chave estrangeira para outros esquemas (DAD-2): sessão, conversa e mensagem são identificadores opacos do Conversations. Guarda o <c>AiTurn</c> (o agrupamento, as tentativas e o cancelamento
/// cooperativo), as versões da definição do agente do tenant e o inbox do módulo (a idempotência por <c>EventId</c> do que ele consome do Conversations).
/// </summary>
public sealed class AgentsDbContext : DbContext
{
    public const string Schema = "agents";

    /// <summary>
    /// A sequência dos tokens de lane dos executores de turno: cada reivindicação pega o próximo valor, então um executor que assume depois sempre leva um token maior que o do anterior, e o
    /// <c>BeginTurn</c> do Conversations recusa o menor. Global (não por tenant): vale também entre turnos da mesma sessão em momentos diferentes.
    /// </summary>
    public const string LaneTokenSequence = "lane_token_seq";

    private readonly IRequestContextAccessor _contexto;

    public AgentsDbContext(DbContextOptions<AgentsDbContext> options, IRequestContextAccessor contexto)
        : base(options)
    {
        _contexto = contexto;
    }

    public DbSet<AiTurn> Turns => Set<AiTurn>();

    public DbSet<AgentDefinitionVersion> DefinitionVersions => Set<AgentDefinitionVersion>();

    public DbSet<KnowledgeGap> KnowledgeGaps => Set<KnowledgeGap>();

    public DbSet<KnowledgeGapOccurrence> KnowledgeGapOccurrences => Set<KnowledgeGapOccurrence>();

    public DbSet<SemanticAnswerSettings> SemanticSettings => Set<SemanticAnswerSettings>();

    public DbSet<SemanticDecision> SemanticDecisions => Set<SemanticDecision>();

    public DbSet<FaqSuggestion> FaqSuggestions => Set<FaqSuggestion>();

    // O EF reavalia esta propriedade a cada execução de consulta (membro do DbContext no filtro global).
    private TenantId TenantAtual => _contexto.GetRequired().TenantId;

    /// <summary>O tenant do contexto confiável, para o SQL escrito à mão (que não passa pelo filtro global): falha fechada sem contexto.</summary>
    internal Guid CurrentTenantId => TenantAtual.Value;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.HasSequence<long>(LaneTokenSequence, Schema).StartsAt(1).IncrementsBy(1);

        modelBuilder.ApplyConfiguration(new AiTurnConfiguration());
        modelBuilder.ApplyConfiguration(new AgentDefinitionVersionConfiguration());
        modelBuilder.ApplyConfiguration(new KnowledgeGapConfiguration());
        modelBuilder.ApplyConfiguration(new KnowledgeGapOccurrenceConfiguration());
        modelBuilder.ApplyConfiguration(new SemanticAnswerSettingsConfiguration());
        modelBuilder.ApplyConfiguration(new SemanticDecisionConfiguration());
        modelBuilder.ApplyConfiguration(new FaqSuggestionConfiguration());

        // Inbox do consumidor (BOUNDARIES.md §12.1): a idempotência por EventId do que este módulo consome do Conversations.
        modelBuilder.ConfigureInbox();

        modelBuilder.Entity<AiTurn>().HasQueryFilter(e => e.TenantId == TenantAtual);
        modelBuilder.Entity<AgentDefinitionVersion>().HasQueryFilter(e => e.TenantId == TenantAtual);
        modelBuilder.Entity<KnowledgeGap>().HasQueryFilter(e => e.TenantId == TenantAtual);
        modelBuilder.Entity<KnowledgeGapOccurrence>().HasQueryFilter(e => e.TenantId == TenantAtual);
        modelBuilder.Entity<SemanticAnswerSettings>().HasQueryFilter(e => e.TenantId == TenantAtual);
        modelBuilder.Entity<SemanticDecision>().HasQueryFilter(e => e.TenantId == TenantAtual);
        modelBuilder.Entity<FaqSuggestion>().HasQueryFilter(e => e.TenantId == TenantAtual);
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
