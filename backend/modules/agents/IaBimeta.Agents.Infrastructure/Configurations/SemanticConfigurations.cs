using IaBimeta.Agents.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IaBimeta.Agents.Infrastructure.Configurations;

/// <summary>A configuracao semantica por tenant (ADR 0034): uma linha por tenant, criada na primeira alteracao do administrador (sem linha = desligado com os limiares padrao).</summary>
internal sealed class SemanticAnswerSettingsConfiguration : IEntityTypeConfiguration<SemanticAnswerSettings>
{
    public void Configure(EntityTypeBuilder<SemanticAnswerSettings> builder)
    {
        builder.ToTable("semantic_answer_settings", table =>
        {
            table.HasCheckConstraint("ck_semantic_answer_settings_mode", "mode IN (0, 1, 2)");
            table.HasCheckConstraint("ck_semantic_answer_settings_min_score", "min_score >= 0.6 AND min_score <= 1");
            table.HasCheckConstraint("ck_semantic_answer_settings_min_margin", "min_margin >= 0 AND min_margin <= 0.5");
            table.HasCheckConstraint("ck_semantic_answer_settings_revision", "revision >= 1");
        });

        builder.HasKey(s => s.TenantId);
        builder.Property(s => s.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(s => s.Mode).HasColumnName("mode").HasConversion<short>();
        builder.Property(s => s.MinScore).HasColumnName("min_score");
        builder.Property(s => s.MinMargin).HasColumnName("min_margin");
        builder.Property(s => s.UpdatedAt).HasColumnName("updated_at");
        builder.Property(s => s.UpdatedBy).HasColumnName("updated_by");
        builder.Property(s => s.Revision).HasColumnName("revision").IsConcurrencyToken();
    }
}

/// <summary>A decisao semantica de um turno (ADR 0034): chave (tenant, turno), sem texto do cliente. Serve as metricas e a calibracao dos limiares.</summary>
internal sealed class SemanticDecisionConfiguration : IEntityTypeConfiguration<SemanticDecision>
{
    public void Configure(EntityTypeBuilder<SemanticDecision> builder)
    {
        builder.ToTable("semantic_decisions", table =>
        {
            table.HasCheckConstraint("ck_semantic_decisions_mode", "mode IN (1, 2)");
            table.HasCheckConstraint("ck_semantic_decisions_outcome", "outcome IN (1, 2, 3, 4, 5, 6, 7, 8, 9)");
            table.HasCheckConstraint("ck_semantic_decisions_times", "embedding_ms >= 0 AND search_ms >= 0");
        });

        builder.HasKey(d => new { d.TenantId, d.TurnId });
        builder.Property(d => d.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(d => d.TurnId).HasColumnName("turn_id").ValueGeneratedNever();
        builder.Property(d => d.DecidedAt).HasColumnName("decided_at");
        builder.Property(d => d.Mode).HasColumnName("mode").HasConversion<short>();
        builder.Property(d => d.Outcome).HasColumnName("outcome").HasConversion<short>();
        builder.Property(d => d.EntryOrdinal).HasColumnName("entry_ordinal");
        builder.Property(d => d.Score).HasColumnName("score");
        builder.Property(d => d.Margin).HasColumnName("margin");
        builder.Property(d => d.DefinitionNumber).HasColumnName("definition_number");
        builder.Property(d => d.EmbeddingMilliseconds).HasColumnName("embedding_ms");
        builder.Property(d => d.SearchMilliseconds).HasColumnName("search_ms");
        builder.Property(d => d.FailureCode).HasColumnName("failure_code").HasMaxLength(60);

        builder.HasIndex(d => new { d.TenantId, d.DecidedAt }).HasDatabaseName("ix_semantic_decisions_tenant_id_decided_at");
    }
}
