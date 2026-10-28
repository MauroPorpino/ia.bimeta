using IaBimeta.Conversations.Domain;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IaBimeta.Conversations.Infrastructure.Configurations;

internal sealed class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public const string ProviderEventIndex = "ix_messages_tenant_id_channel_instance_id_provider_event_id";
    public const string OneReplyPerTurnIndex = "ix_messages_one_reply_per_turn";

    public void Configure(EntityTypeBuilder<Message> builder)
    {
        builder.ToTable("messages", table =>
        {
            // Forma de cada direção: a do cliente tem InboundSeq, evento do provedor e autor cliente; a de saída tem estado de entrega.
            table.HasCheckConstraint(
                "ck_messages_direction_shape",
                "(direction = 1 AND inbound_seq IS NOT NULL AND provider_event_id IS NOT NULL AND author = 1 AND delivery_state IS NULL) " +
                "OR (direction = 2 AND inbound_seq IS NULL AND delivery_state IS NOT NULL AND author IN (2, 3))");
            table.HasCheckConstraint("ck_messages_sequences", "message_seq >= 1 AND (inbound_seq IS NULL OR inbound_seq >= 1)");

            // O idioma da mensagem e a origem andam juntos: desconhecido (os dois nulos) ou declarado/pretendido (os dois preenchidos).
            table.HasCheckConstraint(
                "ck_messages_locale",
                $"(locale IS NULL AND locale_origin IS NULL) OR (locale IS NOT NULL AND locale_origin IS NOT NULL AND locale_origin IN (1, 2) AND locale ~ {LocaleConverter.SqlPattern})");
        });

        // TEN-3: a chave composta começa por tenant_id. O filtro global por tenant é aplicado no DbContext.
        builder.HasKey(m => new { m.TenantId, m.Id });
        builder.Property(m => m.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(m => m.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(m => m.SessionId).HasColumnName("session_id");
        builder.Property(m => m.ConversationId).HasColumnName("conversation_id");
        builder.Property(m => m.ChannelInstanceId).HasColumnName("channel_instance_id").HasConversion(new ChannelInstanceIdConverter());
        builder.Property(m => m.Direction).HasColumnName("direction").HasConversion<short>();
        builder.Property(m => m.MessageSeq).HasColumnName("message_seq");
        builder.Property(m => m.InboundSeq).HasColumnName("inbound_seq");
        builder.Property(m => m.Author).HasColumnName("author").HasConversion<short>();
        builder.Property(m => m.AuthorRef).HasColumnName("author_ref");
        builder.Property(m => m.Kind).HasColumnName("kind").HasConversion<short>();
        builder.Property(m => m.ContentState).HasColumnName("content_state").HasConversion<short>();
        builder.Property(m => m.Text).HasColumnName("text").HasMaxLength(ConversationLimits.TextMaxLength);
        builder.Property(m => m.ProviderEventId).HasColumnName("provider_event_id").HasMaxLength(ConversationLimits.ProviderEventIdMaxLength);
        builder.Property(m => m.ProviderTimestamp).HasColumnName("provider_timestamp");
        builder.Property(m => m.ReceivedAt).HasColumnName("received_at");
        builder.Property(m => m.OccurredAt).HasColumnName("occurred_at");
        builder.Property(m => m.LateArrival).HasColumnName("late_arrival");
        builder.Property(m => m.TurnId).HasColumnName("turn_id");
        builder.Property(m => m.ReplyKind).HasColumnName("reply_kind").HasConversion<short?>();
        builder.Property(m => m.CoveredThroughSeq).HasColumnName("covered_through_seq");
        builder.Property(m => m.DeliveryState).HasColumnName("delivery_state").HasConversion<short?>();
        builder.Property(m => m.DeliveryFailureReason).HasColumnName("delivery_failure_reason").HasMaxLength(ConversationLimits.ReasonMaxLength);
        builder.Property(m => m.ResendOfMessageId).HasColumnName("resend_of_message_id");
        builder.Property(m => m.ResendRequestId).HasColumnName("resend_request_id");
        builder.Property(m => m.RequestedBy).HasColumnName("requested_by");
        builder.Property(m => m.AiRequestIds).HasColumnName("ai_request_ids");
        builder.Property(m => m.CreatedAt).HasColumnName("created_at");
        builder.Property(m => m.Locale).HasColumnName("locale").HasMaxLength(Locale.MaxLength).HasConversion(typeof(LocaleConverter));
        builder.Property(m => m.LocaleOrigin).HasColumnName("locale_origin").HasConversion<short?>();

        // As referências carregam o tenant: o banco recusa mensagem ligada à sessão ou conversa de outro tenant.
        builder.HasOne<ServiceSession>().WithMany().HasForeignKey(m => new { m.TenantId, m.SessionId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Conversation>().WithMany().HasForeignKey(m => new { m.TenantId, m.ConversationId }).OnDelete(DeleteBehavior.Restrict);

        // Ordem total por sessão: nenhum número de sequência se repete, nem do histórico nem do cliente.
        builder.HasIndex(m => new { m.TenantId, m.SessionId, m.MessageSeq }).IsUnique().HasDatabaseName("ix_messages_tenant_id_session_id_message_seq");
        builder.HasIndex(m => new { m.TenantId, m.SessionId, m.InboundSeq }).IsUnique().HasFilter("inbound_seq IS NOT NULL").HasDatabaseName("ix_messages_tenant_id_session_id_inbound_seq");

        // Idempotência: o mesmo evento do provedor, no mesmo canal, grava uma só mensagem.
        builder.HasIndex(m => new { m.TenantId, m.ChannelInstanceId, m.ProviderEventId }).IsUnique().HasFilter("provider_event_id IS NOT NULL").HasDatabaseName(ProviderEventIndex);

        // I5: no máximo uma resposta aceita por turno.
        builder.HasIndex(m => new { m.TenantId, m.SessionId, m.TurnId }).IsUnique().HasFilter("turn_id IS NOT NULL").HasDatabaseName(OneReplyPerTurnIndex);

        // COV-7.3: o mesmo requestId de reenvio gera uma só solicitação.
        builder.HasIndex(m => new { m.TenantId, m.ResendRequestId }).IsUnique().HasFilter("resend_request_id IS NOT NULL").HasDatabaseName("ix_messages_tenant_id_resend_request_id");
        builder.HasIndex(m => new { m.TenantId, m.ResendOfMessageId }).HasFilter("resend_of_message_id IS NOT NULL").HasDatabaseName("ix_messages_tenant_id_resend_of_message_id");

        // O histórico paginado por (OccurredAt, MessageSeq).
        builder.HasIndex(m => new { m.TenantId, m.SessionId, m.OccurredAt, m.MessageSeq }).HasDatabaseName("ix_messages_history");
    }
}
