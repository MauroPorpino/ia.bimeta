using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Domain;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IaBimeta.Conversations.Infrastructure.Configurations;

internal sealed class ContactConfiguration : IEntityTypeConfiguration<Contact>
{
    public const string UniqueRefIndex = "ix_contacts_tenant_id_channel_instance_id_external_ref";

    public void Configure(EntityTypeBuilder<Contact> builder)
    {
        builder.ToTable("contacts", table => table.HasCheckConstraint(
            "ck_contacts_preferred_locale",
            $"preferred_locale IS NULL OR preferred_locale ~ {LocaleConverter.SqlPattern}"));

        // TEN-3: a chave composta começa por tenant_id. O filtro global por tenant é aplicado no DbContext.
        builder.HasKey(c => new { c.TenantId, c.Id });
        builder.Property(c => c.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(c => c.ChannelInstanceId).HasColumnName("channel_instance_id").HasConversion(new ChannelInstanceIdConverter());
        builder.Property(c => c.ExternalRef).HasColumnName("external_ref").HasMaxLength(ConversationLimits.ContactRefMaxLength).IsRequired();
        builder.Property(c => c.DisplayName).HasColumnName("display_name").HasMaxLength(ConversationLimits.DisplayNameMaxLength);
        builder.Property(c => c.CreatedAt).HasColumnName("created_at");
        builder.Property(c => c.PreferredLocale).HasColumnName("preferred_locale").HasMaxLength(Locale.MaxLength).HasConversion(typeof(LocaleConverter));

        // Um contato por (tenant, canal, referência no provedor): é o que evita duas sessões para o mesmo contato novo.
        builder.HasIndex(c => new { c.TenantId, c.ChannelInstanceId, c.ExternalRef }).IsUnique().HasDatabaseName(UniqueRefIndex);
    }
}

internal sealed class ServiceSessionConfiguration : IEntityTypeConfiguration<ServiceSession>
{
    public const string OneOpenPerContactIndex = "ix_service_sessions_one_open_per_contact";

    public void Configure(EntityTypeBuilder<ServiceSession> builder)
    {
        builder.ToTable("service_sessions", table =>
        {
            // Defesa no banco das invariantes da cobertura e da versão: o aplicativo as mantém, o banco recusa a violação.
            table.HasCheckConstraint(
                "ck_service_sessions_sequences",
                "answered_through_seq >= 0 AND answered_through_seq <= last_inbound_seq AND last_inbound_seq <= last_message_seq");
            table.HasCheckConstraint("ck_service_sessions_versions", "state_version >= 1 AND turn_epoch >= 0 AND last_lease_token >= 0");
            table.HasCheckConstraint("ck_service_sessions_status", "status IN (1, 2) AND handling_mode IN (1, 2, 3)");
            table.HasCheckConstraint("ck_service_sessions_responder", "handling_mode = 3 OR responder_ref IS NULL");

            // Um setor só existe dentro de uma unidade.
            table.HasCheckConstraint("ck_service_sessions_department", "department_id IS NULL OR business_unit_id IS NOT NULL");
        });

        // TEN-3: a chave composta começa por tenant_id. O filtro global por tenant é aplicado no DbContext.
        builder.HasKey(s => new { s.TenantId, s.Id });
        builder.Property(s => s.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(s => s.OrganizationId).HasColumnName("organization_id");
        builder.Property(s => s.BusinessUnitId).HasColumnName("business_unit_id");
        builder.Property(s => s.DepartmentId).HasColumnName("department_id");
        builder.Property(s => s.ContactId).HasColumnName("contact_id");
        builder.Property(s => s.ChannelInstanceId).HasColumnName("channel_instance_id").HasConversion(new ChannelInstanceIdConverter());
        builder.Property(s => s.Status).HasColumnName("status").HasConversion<short>();
        builder.Property(s => s.HandlingMode).HasColumnName("handling_mode").HasConversion<short>();
        builder.Property(s => s.ResponderRef).HasColumnName("responder_ref");
        builder.Property(s => s.StateVersion).HasColumnName("state_version");
        builder.Property(s => s.LastMessageSeq).HasColumnName("last_message_seq");
        builder.Property(s => s.LastInboundSeq).HasColumnName("last_inbound_seq");
        builder.Property(s => s.AnsweredThroughSeq).HasColumnName("answered_through_seq");
        builder.Property(s => s.ActiveTurnId).HasColumnName("active_turn_id");
        builder.Property(s => s.TurnEpoch).HasColumnName("turn_epoch");
        builder.Property(s => s.LastLeaseToken).HasColumnName("last_lease_token");
        builder.Property(s => s.HandlingFenceToken).HasColumnName("handling_fence_token").HasDefaultValue(0L);
        builder.Property(s => s.HandlingPeriod).HasColumnName("handling_period").HasDefaultValue(0L);
        builder.Property(s => s.PlacementEpoch).HasColumnName("placement_epoch").HasDefaultValue(1L);
        builder.Property(s => s.LastReplyAcceptedAt).HasColumnName("last_reply_accepted_at");
        builder.Property(s => s.ReplyDeliveryFailed).HasColumnName("reply_delivery_failed");
        builder.Property(s => s.AttentionRaisedForSeq).HasColumnName("attention_raised_for_seq");
        builder.Property(s => s.CreatedAt).HasColumnName("created_at");
        builder.Property(s => s.LastMessageAt).HasColumnName("last_message_at");
        builder.Property(s => s.ClosedAt).HasColumnName("closed_at");

        // A referência ao contato carrega o tenant: o banco recusa sessão ligada ao contato de outro tenant.
        builder.HasOne<Contact>().WithMany().HasForeignKey(s => new { s.TenantId, s.ContactId }).OnDelete(DeleteBehavior.Restrict);

        // No máximo uma sessão ABERTA por contato (status 1 = Open).
        builder.HasIndex(s => new { s.TenantId, s.ContactId }).IsUnique().HasFilter("status = 1").HasDatabaseName(OneOpenPerContactIndex);

        // A listagem por organização, da mais recente para a mais antiga.
        builder.HasIndex(s => new { s.TenantId, s.OrganizationId, s.LastMessageAt }).HasDatabaseName("ix_service_sessions_tenant_id_organization_id_last_message_at");
    }
}

internal sealed class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public const string UniqueChannelIndex = "ix_conversations_tenant_id_session_id_channel_instance_id";

    public void Configure(EntityTypeBuilder<Conversation> builder)
    {
        builder.ToTable("conversations", table => table.HasCheckConstraint(
            "ck_conversations_locale",
            $"effective_locale ~ {LocaleConverter.SqlPattern} AND locale_source IN (1, 2, 3, 4, 5, 6)"));

        builder.HasKey(c => new { c.TenantId, c.Id });
        builder.Property(c => c.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(c => c.SessionId).HasColumnName("session_id");
        builder.Property(c => c.ContactId).HasColumnName("contact_id");
        builder.Property(c => c.ChannelInstanceId).HasColumnName("channel_instance_id").HasConversion(new ChannelInstanceIdConverter());
        builder.Property(c => c.CreatedAt).HasColumnName("created_at");

        // O idioma efetivo é desta conversa, independente do tenant e das outras conversas; a origem diz se foi suposição.
        builder.Property(c => c.EffectiveLocale).HasColumnName("effective_locale").HasMaxLength(Locale.MaxLength).HasConversion(new LocaleConverter()).IsRequired();
        builder.Property(c => c.LocaleSource).HasColumnName("locale_source").HasConversion<short>();

        builder.HasOne<ServiceSession>().WithMany().HasForeignKey(c => new { c.TenantId, c.SessionId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Contact>().WithMany().HasForeignKey(c => new { c.TenantId, c.ContactId }).OnDelete(DeleteBehavior.Restrict);

        // Uma conversa por sessão e canal.
        builder.HasIndex(c => new { c.TenantId, c.SessionId, c.ChannelInstanceId }).IsUnique().HasDatabaseName(UniqueChannelIndex);
    }
}

internal sealed class HandlingTransitionConfiguration : IEntityTypeConfiguration<HandlingTransition>
{
    public void Configure(EntityTypeBuilder<HandlingTransition> builder)
    {
        builder.ToTable("handling_transitions");

        // A chave é a própria chave de idempotência da transição.
        builder.HasKey(t => new { t.TenantId, t.SessionId, t.TransitionId });
        builder.Property(t => t.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(t => t.SessionId).HasColumnName("session_id");
        builder.Property(t => t.TransitionId).HasColumnName("transition_id");
        builder.Property(t => t.StateVersionAfter).HasColumnName("state_version_after");
        builder.Property(t => t.MessageSeqAt).HasColumnName("message_seq_at");
        builder.Property(t => t.HandlingPeriodAfter).HasColumnName("handling_period_after");
        builder.Property(t => t.AppliedAt).HasColumnName("applied_at");

        builder.HasOne<ServiceSession>().WithMany().HasForeignKey(t => new { t.TenantId, t.SessionId }).OnDelete(DeleteBehavior.Restrict);
    }
}
