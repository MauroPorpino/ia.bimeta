using IaBimeta.Workflows.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IaBimeta.Workflows.Infrastructure.Configurations;

internal sealed class WorkflowVersionConfiguration : IEntityTypeConfiguration<WorkflowVersion>
{
    public const string PublishedIndex = "ux_workflow_versions_published";
    public const string DraftIndex = "ux_workflow_versions_draft";
    public const string VersionIndex = "ux_workflow_versions_version";

    public void Configure(EntityTypeBuilder<WorkflowVersion> builder)
    {
        builder.ToTable("workflow_versions", table =>
        {
            table.HasCheckConstraint("ck_workflow_versions_status", "status IN (1, 2, 3)");
            table.HasCheckConstraint("ck_workflow_versions_version", "version >= 1 AND revision >= 1");
            table.HasCheckConstraint("ck_workflow_versions_graph", "octet_length(graph_json) <= 4194304");
            table.HasCheckConstraint("ck_workflow_versions_published", "(status = 1) = (published_at IS NULL)");
            table.HasCheckConstraint("ck_workflow_versions_retired", "(status = 3) = (retired_at IS NOT NULL)");
        });

        builder.HasKey(v => new { v.TenantId, v.Id });
        builder.Property(v => v.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(v => v.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(v => v.OrganizationId).HasColumnName("organization_id");
        builder.Property(v => v.Key).HasColumnName("key").HasMaxLength(80);
        builder.Property(v => v.Version).HasColumnName("version");
        builder.Property(v => v.Status).HasColumnName("status").HasConversion<short>();
        builder.Property(v => v.Name).HasColumnName("name").HasMaxLength(100);
        builder.Property(v => v.Description).HasColumnName("description").HasMaxLength(500);
        builder.Property(v => v.GraphJson).HasColumnName("graph_json");
        builder.Property(v => v.GraphHash).HasColumnName("graph_hash").HasMaxLength(64);
        builder.Property(v => v.CreatedBy).HasColumnName("created_by");
        builder.Property(v => v.CreatedAt).HasColumnName("created_at");
        builder.Property(v => v.UpdatedAt).HasColumnName("updated_at");
        builder.Property(v => v.PublishedAt).HasColumnName("published_at");
        builder.Property(v => v.PublishedBy).HasColumnName("published_by");
        builder.Property(v => v.RetiredAt).HasColumnName("retired_at");
        builder.Property(v => v.Revision).HasColumnName("revision").IsConcurrencyToken();

        builder.HasIndex(v => new { v.TenantId, v.Key, v.Version }).IsUnique().HasDatabaseName(VersionIndex);
        builder.HasIndex(v => new { v.TenantId, v.Key }).IsUnique().HasFilter("status = 2").HasDatabaseName(PublishedIndex);
        // O índice do rascunho inclui o status nas colunas: sem isso o EF funde os dois índices parciais (mesmas colunas) e o da versão publicada se perde.
        builder.HasIndex(v => new { v.TenantId, v.Key, v.Status }).IsUnique().HasFilter("status = 1").HasDatabaseName(DraftIndex);
        builder.HasIndex(v => new { v.TenantId, v.OrganizationId }).HasDatabaseName("ix_workflow_versions_tenant_id_organization_id");
    }
}
