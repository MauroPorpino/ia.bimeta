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

/// <summary>
/// O estado de envio de cada conta (<c>channels.account_send_state</c>): balde de fichas (SND-2), espera do provedor e pausa (SND-6). Uma linha por conta, criada na primeira necessidade. Os nomes das colunas
/// são usados pelo <c>INSERT ... ON CONFLICT</c> da persistência (a linha nasce cheia), então mudar aqui exige mudar lá.
/// </summary>
internal sealed class AccountSendStateConfiguration : IEntityTypeConfiguration<AccountSendState>
{
    public const string TableName = "account_send_state";

    public void Configure(EntityTypeBuilder<AccountSendState> builder)
    {
        builder.ToTable(TableName, table =>
        {
            table.HasCheckConstraint("ck_account_send_state_tokens", "tokens >= 0");
            table.HasCheckConstraint("ck_account_send_state_throughput", "throughput_per_second IS NULL OR (throughput_per_second >= 1 AND throughput_per_second <= 1000)");
            table.HasCheckConstraint("ck_account_send_state_pause", "(is_paused AND pause_reason IS NOT NULL AND paused_at IS NOT NULL) OR (NOT is_paused AND pause_reason IS NULL)");
        });

        // TEN-3: a chave começa por tenant_id. O filtro global por tenant é aplicado no DbContext.
        builder.HasKey(s => new { s.TenantId, s.AccountId });
        builder.Property(s => s.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(s => s.AccountId).HasColumnName("account_id").ValueGeneratedNever();
        builder.Property(s => s.Tokens).HasColumnName("tokens");
        builder.Property(s => s.RefilledAt).HasColumnName("refilled_at");
        builder.Property(s => s.CooldownUntil).HasColumnName("cooldown_until");
        builder.Property(s => s.IsPaused).HasColumnName("is_paused");
        builder.Property(s => s.PauseReason).HasColumnName("pause_reason").HasMaxLength(AccountSendState.PauseReasonMaxLength);
        builder.Property(s => s.PausedAt).HasColumnName("paused_at");
        builder.Property(s => s.ResumedAt).HasColumnName("resumed_at");
        builder.Property(s => s.ThroughputPerSecond).HasColumnName("throughput_per_second");
        builder.Property(s => s.UpdatedAt).HasColumnName("updated_at");

        // A conta é do mesmo tenant: o banco garante que o estado não aponta para conta de outro.
        builder.HasOne<ChannelAccount>().WithOne()
            .HasForeignKey<AccountSendState>(s => new { s.TenantId, s.AccountId })
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
