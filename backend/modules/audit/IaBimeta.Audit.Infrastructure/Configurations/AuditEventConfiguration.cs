using IaBimeta.Audit.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IaBimeta.Audit.Infrastructure.Configurations;

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.ToTable("audit_events");

        // TEN-3: a chave composta começa por tenant_id. O filtro global por tenant é aplicado no DbContext.
        builder.HasKey(e => new { e.TenantId, e.Id });
        builder.Property(e => e.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(e => e.OccurredAt).HasColumnName("occurred_at");
        builder.Property(e => e.ActorUserId).HasColumnName("actor_user_id");
        builder.Property(e => e.ActorOrigin).HasColumnName("actor_origin").HasMaxLength(AuditEvent.ActorOriginMaxLength).IsRequired();
        builder.Property(e => e.Operation).HasColumnName("operation").HasMaxLength(AuditEvent.OperationMaxLength).IsRequired();
        builder.Property(e => e.ResourceType).HasColumnName("resource_type").HasMaxLength(AuditEvent.ResourceTypeMaxLength).IsRequired();
        builder.Property(e => e.ResourceId).HasColumnName("resource_id").HasMaxLength(AuditEvent.ResourceIdMaxLength);
        builder.Property(e => e.Outcome).HasColumnName("outcome").HasConversion<short>();

        builder.HasIndex(e => new { e.TenantId, e.OccurredAt }).HasDatabaseName("ix_audit_events_tenant_id_occurred_at");
    }
}
