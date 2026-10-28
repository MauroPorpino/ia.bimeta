using IaBimeta.Conversations.Domain;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IaBimeta.Conversations.Infrastructure.Configurations;

internal sealed class MessageTranslationConfiguration : IEntityTypeConfiguration<MessageTranslation>
{
    public const string UniquePerTargetIndex = "ix_message_translations_tenant_id_message_id_target_locale";

    public void Configure(EntityTypeBuilder<MessageTranslation> builder)
    {
        builder.ToTable("message_translations", table =>
        {
            table.HasCheckConstraint(
                "ck_message_translations_locales",
                $"target_locale ~ {LocaleConverter.SqlPattern} AND (source_locale IS NULL OR source_locale ~ {LocaleConverter.SqlPattern})");
        });

        // TEN-3: a chave composta começa por tenant_id. O filtro global por tenant é aplicado no DbContext.
        builder.HasKey(t => new { t.TenantId, t.Id });
        builder.Property(t => t.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(t => t.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(t => t.MessageId).HasColumnName("message_id");
        builder.Property(t => t.TargetLocale).HasColumnName("target_locale").HasMaxLength(Locale.MaxLength).HasConversion(new LocaleConverter()).IsRequired();
        builder.Property(t => t.SourceLocale).HasColumnName("source_locale").HasMaxLength(Locale.MaxLength).HasConversion(typeof(LocaleConverter));
        builder.Property(t => t.Text).HasColumnName("text").HasMaxLength(ConversationLimits.TextMaxLength).IsRequired();
        builder.Property(t => t.Provenance).HasColumnName("provenance").HasMaxLength(ConversationLimits.ReasonMaxLength).IsRequired();
        builder.Property(t => t.CreatedAt).HasColumnName("created_at");

        // A referência carrega o tenant: o banco recusa tradução ligada à mensagem de outro tenant. A tradução fica em tabela
        // própria, então nenhuma gravação dela alcança o texto original da mensagem.
        builder.HasOne<Message>().WithMany().HasForeignKey(t => new { t.TenantId, t.MessageId }).OnDelete(DeleteBehavior.Restrict);

        // No máximo uma tradução por mensagem e idioma de destino: uma segunda nunca sobrescreve a primeira.
        builder.HasIndex(t => new { t.TenantId, t.MessageId, t.TargetLocale }).IsUnique().HasDatabaseName(UniquePerTargetIndex);
    }
}
