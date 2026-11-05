using IaBimeta.Channels.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IaBimeta.Channels.Infrastructure.Configurations;

/// <summary>O identificador opaco do webhook de cada conta. Sem filtro de tenant na leitura (a URL é tudo o que há antes de saber o tenant): só chaves de roteamento.</summary>
internal sealed class ChannelWebhookConfiguration : IEntityTypeConfiguration<ChannelWebhook>
{
    public const string UniqueWebhookIndex = "ux_channel_webhooks_webhook_id";

    public void Configure(EntityTypeBuilder<ChannelWebhook> builder)
    {
        builder.ToTable("channel_webhooks");

        // TEN-3: a chave começa por tenant_id. Uma conta tem um só identificador; o identificador é único na plataforma toda.
        builder.HasKey(w => new { w.TenantId, w.AccountId });
        builder.Property(w => w.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(w => w.AccountId).HasColumnName("account_id").ValueGeneratedNever();
        builder.Property(w => w.WebhookId).HasColumnName("webhook_id");
        builder.Property(w => w.CreatedAt).HasColumnName("created_at");

        builder.HasIndex(w => w.WebhookId).IsUnique().HasDatabaseName(UniqueWebhookIndex);

        // A conta é do mesmo tenant: o banco garante que o identificador não aponta para conta de outro.
        builder.HasOne<ChannelAccount>().WithOne()
            .HasForeignKey<ChannelWebhook>(w => new { w.TenantId, w.AccountId })
            .HasPrincipalKey<ChannelAccount>(a => new { a.TenantId, a.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>O despacho das mensagens de saída (<c>channels.outbound_messages</c>).</summary>
internal sealed class OutboundMessageConfiguration : IEntityTypeConfiguration<OutboundMessage>
{
    public const string UniqueOperationIndex = "ux_outbound_messages_tenant_id_operation_id";
    public const string ProviderMessageIndex = "ix_outbound_messages_tenant_id_provider_message_id";

    public void Configure(EntityTypeBuilder<OutboundMessage> builder)
    {
        builder.ToTable("outbound_messages", table =>
        {
            table.HasCheckConstraint("ck_outbound_messages_state", "state IN (1, 2, 3, 4, 5)");
            table.HasCheckConstraint("ck_outbound_messages_attempts", "attempts >= 0");
        });

        // TEN-3: a chave começa por tenant_id. O filtro global por tenant é aplicado no DbContext.
        builder.HasKey(m => new { m.TenantId, m.Id });
        builder.Property(m => m.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(m => m.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(m => m.OperationId).HasColumnName("operation_id");
        builder.Property(m => m.ChannelInstanceId).HasColumnName("channel_instance_id").HasConversion(new ChannelInstanceIdConverter());
        builder.Property(m => m.State).HasColumnName("state").HasConversion<short>();
        builder.Property(m => m.ProviderMessageId).HasColumnName("provider_message_id").HasMaxLength(200);
        builder.Property(m => m.Attempts).HasColumnName("attempts");
        builder.Property(m => m.ErrorCode).HasColumnName("error_code").HasMaxLength(100);
        builder.Property(m => m.Notified).HasColumnName("notified");
        builder.Property(m => m.CreatedAt).HasColumnName("created_at");
        builder.Property(m => m.UpdatedAt).HasColumnName("updated_at");

        // A chave de idempotência do efeito externo: uma intenção por operação e tenant.
        builder.HasIndex(m => new { m.TenantId, m.OperationId }).IsUnique().HasDatabaseName(UniqueOperationIndex);
        builder.HasIndex(m => new { m.TenantId, m.ProviderMessageId }).HasDatabaseName(ProviderMessageIndex);
    }
}
