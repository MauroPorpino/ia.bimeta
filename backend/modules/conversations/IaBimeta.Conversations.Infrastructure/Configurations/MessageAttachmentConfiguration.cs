using IaBimeta.Conversations.Domain;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IaBimeta.Conversations.Infrastructure.Configurations;

internal sealed class MessageAttachmentConfiguration : IEntityTypeConfiguration<MessageAttachment>
{
    public const string OnePerMessageIndex = "ux_message_attachments_tenant_id_message_id";

    public void Configure(EntityTypeBuilder<MessageAttachment> builder)
    {
        builder.ToTable("message_attachments", table =>
        {
            table.HasCheckConstraint("ck_message_attachments_kind", "kind IN (2, 3, 4)");
            table.HasCheckConstraint("ck_message_attachments_status", "status IN (1, 2, 3, 4)");

            // O conteúdo guardado e o estado "disponível" andam juntos: só o disponível tem chave, tipo verificado, tamanho e resumo; o indisponível e o recusado têm o motivo.
            table.HasCheckConstraint(
                "ck_message_attachments_available",
                "(status = 2 AND storage_key IS NOT NULL AND mime_type IS NOT NULL AND size_bytes IS NOT NULL AND size_bytes >= 1 AND sha256 IS NOT NULL AND failure_code IS NULL) " +
                "OR (status <> 2 AND storage_key IS NULL AND mime_type IS NULL AND size_bytes IS NULL AND sha256 IS NULL)");
            table.HasCheckConstraint("ck_message_attachments_failure", "(status IN (3, 4) AND failure_code IS NOT NULL) OR (status IN (1, 2) AND failure_code IS NULL)");

            // ADR 0031: o arquivo é do cliente (1, entrada) ou do atendente (2, saída). O do cliente tem o identificador da mídia no provedor e passa por Pending; o do atendente não tem identificador e já nasce guardado.
            table.HasCheckConstraint("ck_message_attachments_direction", "direction IN (1, 2)");
            table.HasCheckConstraint("ck_message_attachments_provider_media", "(direction = 1 AND provider_media_id IS NOT NULL) OR (direction = 2 AND provider_media_id IS NULL)");
            table.HasCheckConstraint("ck_message_attachments_outbound_stored", "direction = 1 OR status <> 1");

            // ADR 0032: o estado da varredura de código malicioso (0 = não verificado, 1 = limpo).
            table.HasCheckConstraint("ck_message_attachments_scan_status", "scan_status IN (0, 1)");
        });

        builder.HasKey(a => new { a.TenantId, a.Id });
        builder.Property(a => a.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(a => a.MessageId).HasColumnName("message_id");
        builder.Property(a => a.SessionId).HasColumnName("session_id");
        builder.Property(a => a.Kind).HasColumnName("kind").HasConversion<short>();
        builder.Property(a => a.Direction).HasColumnName("direction").HasConversion<short>();
        builder.Property(a => a.Status).HasColumnName("status").HasConversion<short>();
        builder.Property(a => a.ProviderMediaId).HasColumnName("provider_media_id").HasMaxLength(MessageAttachment.ProviderMediaIdMaxLength);
        builder.Property(a => a.DeclaredMimeType).HasColumnName("declared_mime_type").HasMaxLength(MessageAttachment.MimeTypeMaxLength);
        builder.Property(a => a.MimeType).HasColumnName("mime_type").HasMaxLength(MessageAttachment.MimeTypeMaxLength);
        builder.Property(a => a.FileName).HasColumnName("file_name").HasMaxLength(MessageAttachment.FileNameMaxLength);
        builder.Property(a => a.Caption).HasColumnName("caption").HasMaxLength(MessageAttachment.CaptionMaxLength);
        builder.Property(a => a.SizeBytes).HasColumnName("size_bytes");
        builder.Property(a => a.Sha256).HasColumnName("sha256").HasMaxLength(64);
        builder.Property(a => a.StorageKey).HasColumnName("storage_key").HasMaxLength(MessageAttachment.StorageKeyMaxLength);
        builder.Property(a => a.FailureCode).HasColumnName("failure_code").HasMaxLength(MessageAttachment.FailureCodeMaxLength);
        builder.Property(a => a.ScanStatus).HasColumnName("scan_status").HasConversion<short>();
        builder.Property(a => a.ScannedAt).HasColumnName("scanned_at");
        builder.Property(a => a.CreatedAt).HasColumnName("created_at");
        builder.Property(a => a.UpdatedAt).HasColumnName("updated_at");

        // O arquivo é da mensagem E do mesmo tenant: o banco recusa arquivo ligado à mensagem de outro tenant. Um por mensagem.
        builder.HasOne<Message>().WithMany().HasForeignKey(a => new { a.TenantId, a.MessageId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(a => new { a.TenantId, a.MessageId }).IsUnique().HasDatabaseName(OnePerMessageIndex);
        builder.HasIndex(a => new { a.TenantId, a.SessionId }).HasDatabaseName("ix_message_attachments_tenant_id_session_id");
    }
}
