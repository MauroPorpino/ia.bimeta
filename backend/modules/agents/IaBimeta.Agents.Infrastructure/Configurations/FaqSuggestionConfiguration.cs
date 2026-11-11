using IaBimeta.Agents.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IaBimeta.Agents.Infrastructure.Configurations;

/// <summary>As sugestoes da IA local para o FAQ (ADR 0034). Pendente se, e somente se, ainda nao decidida; uma sugestao pendente por chave de deduplicacao (indice unico parcial).</summary>
internal sealed class FaqSuggestionConfiguration : IEntityTypeConfiguration<FaqSuggestion>
{
    public const string PendingKeyIndex = "ux_faq_suggestions_pending_key";

    public void Configure(EntityTypeBuilder<FaqSuggestion> builder)
    {
        builder.ToTable("faq_suggestions", table =>
        {
            table.HasCheckConstraint("ck_faq_suggestions_kind", "kind IN (1, 2, 3, 4)");
            table.HasCheckConstraint("ck_faq_suggestions_status", "status IN (1, 2, 3)");
            table.HasCheckConstraint("ck_faq_suggestions_revision", "revision >= 1 AND edits >= 0");
            table.HasCheckConstraint("ck_faq_suggestions_payload", "octet_length(payload_json) <= 8000");
            table.HasCheckConstraint("ck_faq_suggestions_decided", "(status = 1) = (decided_at IS NULL)");
            table.HasCheckConstraint("ck_faq_suggestions_applied", "applied_draft_version IS NULL OR (status = 2 AND applied_draft_version >= 1)");
        });

        builder.HasKey(s => new { s.TenantId, s.Id });
        builder.Property(s => s.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(s => s.Kind).HasColumnName("kind").HasConversion<short>();
        builder.Property(s => s.Status).HasColumnName("status").HasConversion<short>();
        builder.Property(s => s.GapId).HasColumnName("gap_id");
        builder.Property(s => s.DedupeKey).HasColumnName("dedupe_key").HasMaxLength(FaqSuggestion.DedupeKeyMaxLength);
        builder.Property(s => s.PayloadJson).HasColumnName("payload_json").HasMaxLength(FaqSuggestion.PayloadMaxLength);
        builder.Property(s => s.ModelId).HasColumnName("model_id").HasMaxLength(100);
        builder.Property(s => s.CreatedAt).HasColumnName("created_at");
        builder.Property(s => s.CreatedBy).HasColumnName("created_by");
        builder.Property(s => s.DecidedAt).HasColumnName("decided_at");
        builder.Property(s => s.DecidedBy).HasColumnName("decided_by");
        builder.Property(s => s.AppliedDraftVersion).HasColumnName("applied_draft_version");
        builder.Property(s => s.Edits).HasColumnName("edits");
        builder.Property(s => s.Revision).HasColumnName("revision").IsConcurrencyToken();
        builder.Ignore(s => s.Payload);

        builder.HasIndex(s => new { s.TenantId, s.DedupeKey }).IsUnique().HasFilter("status = 1").HasDatabaseName(PendingKeyIndex);
        builder.HasIndex(s => new { s.TenantId, s.Status, s.CreatedAt }).HasDatabaseName("ix_faq_suggestions_tenant_id_status_created_at");
    }
}
