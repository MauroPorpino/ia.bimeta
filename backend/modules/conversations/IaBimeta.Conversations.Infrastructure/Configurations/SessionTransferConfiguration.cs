using IaBimeta.Conversations.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IaBimeta.Conversations.Infrastructure.Configurations;

internal sealed class SessionTransferConfiguration : IEntityTypeConfiguration<SessionTransfer>
{
    public void Configure(EntityTypeBuilder<SessionTransfer> builder)
    {
        builder.ToTable("session_transfers", table =>
        {
            // O setor só existe dentro de uma unidade, na origem e no destino.
            table.HasCheckConstraint(
                "ck_session_transfers_departments",
                "(from_department_id IS NULL OR from_business_unit_id IS NOT NULL) AND (to_department_id IS NULL OR to_business_unit_id IS NOT NULL)");
            table.HasCheckConstraint("ck_session_transfers_state_version", "state_version_after >= 2");
        });

        // A chave é a própria chave de idempotência do encaminhamento (TEN-3: começa por tenant_id).
        builder.HasKey(t => new { t.TenantId, t.SessionId, t.TransferId });
        builder.Property(t => t.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(t => t.SessionId).HasColumnName("session_id");
        builder.Property(t => t.TransferId).HasColumnName("transfer_id");
        builder.Property(t => t.FromBusinessUnitId).HasColumnName("from_business_unit_id");
        builder.Property(t => t.FromDepartmentId).HasColumnName("from_department_id");
        builder.Property(t => t.ToBusinessUnitId).HasColumnName("to_business_unit_id");
        builder.Property(t => t.ToDepartmentId).HasColumnName("to_department_id");
        builder.Property(t => t.RequestedBy).HasColumnName("requested_by");
        builder.Property(t => t.Reason).HasColumnName("reason").HasMaxLength(ConversationLimits.ReasonMaxLength).IsRequired();
        builder.Property(t => t.StateVersionAfter).HasColumnName("state_version_after");
        builder.Property(t => t.TransferredAt).HasColumnName("transferred_at");

        // A referência carrega o tenant: o banco recusa rastro ligado à sessão de outro tenant.
        builder.HasOne<ServiceSession>().WithMany().HasForeignKey(t => new { t.TenantId, t.SessionId }).OnDelete(DeleteBehavior.Restrict);

        // A leitura do rastro de uma sessão, na ordem em que aconteceu.
        builder.HasIndex(t => new { t.TenantId, t.SessionId, t.StateVersionAfter }).IsUnique().HasDatabaseName("ix_session_transfers_tenant_id_session_id_state_version_after");
    }
}
