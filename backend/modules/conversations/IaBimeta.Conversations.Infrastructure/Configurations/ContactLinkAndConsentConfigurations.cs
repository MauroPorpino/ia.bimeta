using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Domain;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IaBimeta.Conversations.Infrastructure.Configurations;

/// <summary>O vínculo entre o cliente do sistema externo e o contato (ADR 0041, seção 2). Sem chave estrangeira entre esquemas: conexão e organização são identificadores opacos; a chave ao contato é dentro do esquema.</summary>
internal sealed class ContactExternalLinkConfiguration : IEntityTypeConfiguration<ContactExternalLink>
{
    /// <summary>No máximo um vínculo ATIVO por (tenant, conexão, cliente, contato): repetir a criação é idempotente; revincular depois de desvincular insere outra linha.</summary>
    public const string ActiveUniqueIndex = "ux_contact_external_links_active";

    public const string ActiveFilter = "unlinked_at IS NULL";

    public const string ByCustomerIndex = "ix_contact_external_links_tenant_id_connection_id_customer_ref";

    public const string ByContactIndex = "ix_contact_external_links_tenant_id_contact_id";

    public void Configure(EntityTypeBuilder<ContactExternalLink> builder)
    {
        builder.ToTable("contact_external_links", table =>
        {
            table.HasCheckConstraint("ck_contact_external_links_customer_ref", "char_length(customer_ref) BETWEEN 1 AND 128");
            table.HasCheckConstraint("ck_contact_external_links_origin", "origin IN (1, 2) AND linked_by_kind IN (1, 2) AND ((origin = 1) = (linked_by_kind = 1))");

            // A desvinculação é tudo ou nada: instante, tipo e autor andam juntos.
            table.HasCheckConstraint(
                "ck_contact_external_links_unlink",
                "(unlinked_at IS NULL AND unlinked_by_kind IS NULL AND unlinked_by_id IS NULL) OR (unlinked_at IS NOT NULL AND unlinked_by_kind IN (1, 2) AND unlinked_by_id IS NOT NULL)");
        });

        // TEN-3: a chave composta começa por tenant_id. O filtro global por tenant é aplicado no DbContext.
        builder.HasKey(l => new { l.TenantId, l.Id });
        builder.Property(l => l.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(l => l.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(l => l.OrganizationId).HasColumnName("organization_id");
        builder.Property(l => l.ConnectionId).HasColumnName("connection_id");
        builder.Property(l => l.CustomerRef).HasColumnName("customer_ref").HasMaxLength(ContactExternalLink.CustomerRefMaxLength).IsRequired();
        builder.Property(l => l.ContactId).HasColumnName("contact_id");
        builder.Property(l => l.Origin).HasColumnName("origin").HasConversion<short>();
        builder.Property(l => l.LinkedAt).HasColumnName("linked_at");
        builder.Property(l => l.LinkedByKind).HasColumnName("linked_by_kind").HasConversion<short>();
        builder.Property(l => l.LinkedById).HasColumnName("linked_by_id");
        builder.Property(l => l.UnlinkedAt).HasColumnName("unlinked_at");
        builder.Property(l => l.UnlinkedByKind).HasColumnName("unlinked_by_kind").HasConversion<short?>();
        builder.Property(l => l.UnlinkedById).HasColumnName("unlinked_by_id");
        builder.Ignore(l => l.IsActive);

        // O contato é do mesmo tenant (o banco recusa vínculo a contato de outro tenant) e nunca é apagado por este vínculo.
        builder.HasOne<Contact>().WithMany().HasForeignKey(l => new { l.TenantId, l.ContactId }).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(l => new { l.TenantId, l.ConnectionId, l.CustomerRef, l.ContactId }).IsUnique().HasFilter(ActiveFilter).HasDatabaseName(ActiveUniqueIndex);
        builder.HasIndex(l => new { l.TenantId, l.ConnectionId, l.CustomerRef }).HasDatabaseName(ByCustomerIndex);
        builder.HasIndex(l => new { l.TenantId, l.ContactId }).HasDatabaseName(ByContactIndex);
    }
}

/// <summary>
/// O consentimento de contato (ADR 0041, seção 4): somente acréscimo. O aplicativo não tem caminho de alteração; o BANCO recusa UPDATE e DELETE por gatilho (migration <c>ContactLinksAndConsents</c>), como defesa
/// adicional. Sem chave estrangeira: o consentimento é do telefone na organização (o cliente pode nunca ter escrito), e a sessão e a mensagem de origem são só referências de rastreio.
/// </summary>
internal sealed class ContactConsentConfiguration : IEntityTypeConfiguration<ContactConsent>
{
    /// <summary>O nome do gatilho que torna a tabela somente de acréscimo (a migration o cria e o <c>Down</c> o remove).</summary>
    public const string AppendOnlyTrigger = "tr_contact_consents_append_only";

    public const string AppendOnlyFunction = "contact_consents_append_only";

    /// <summary>A revogação por palavra-chave é idempotente por (tenant, mensagem de origem, finalidade): a reentrega da mesma mensagem não grava outra linha.</summary>
    public const string KeywordUniqueIndex = "ux_contact_consents_keyword_source";

    public const string KeywordFilter = "origin = 3";

    public const string LatestIndex = "ix_contact_consents_tenant_id_organization_id_phone_purpose_seq";

    public void Configure(EntityTypeBuilder<ContactConsent> builder)
    {
        builder.ToTable("contact_consents", table =>
        {
            table.HasCheckConstraint("ck_contact_consents_phone", $"phone ~ {E164PhoneNumber.SqlPattern}");
            table.HasCheckConstraint("ck_contact_consents_domain", "purpose IN (1, 2) AND status IN (1, 2) AND origin IN (1, 2, 3) AND actor_kind IN (1, 2, 3)");
            table.HasCheckConstraint("ck_contact_consents_evidence", "char_length(btrim(evidence)) BETWEEN 1 AND 2000");
            table.HasCheckConstraint("ck_contact_consents_customer_ref", "customer_ref IS NULL OR char_length(customer_ref) BETWEEN 1 AND 128");

            // A origem e o autor andam juntos; a palavra-chave do cliente é do sistema e traz a sessão e a mensagem que a provam.
            table.HasCheckConstraint(
                "ck_contact_consents_origin_actor",
                "(origin = 1 AND actor_kind = 1 AND actor_id IS NOT NULL) OR (origin = 2 AND actor_kind = 2 AND actor_id IS NOT NULL) OR (origin = 3 AND actor_kind = 3 AND actor_id IS NULL AND session_id IS NOT NULL AND source_message_id IS NOT NULL)");
        });

        // TEN-3: a chave composta começa por tenant_id. O filtro global por tenant é aplicado no DbContext.
        builder.HasKey(c => new { c.TenantId, c.Id });
        builder.Property(c => c.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(c => c.Seq).HasColumnName("seq").UseIdentityAlwaysColumn().ValueGeneratedOnAdd();
        builder.Property(c => c.OrganizationId).HasColumnName("organization_id");
        builder.Property(c => c.Phone).HasColumnName("phone").HasMaxLength(16).IsRequired();
        builder.Property(c => c.Purpose).HasColumnName("purpose").HasConversion<short>();
        builder.Property(c => c.Status).HasColumnName("status").HasConversion<short>();
        builder.Property(c => c.Origin).HasColumnName("origin").HasConversion<short>();
        builder.Property(c => c.Evidence).HasColumnName("evidence").HasMaxLength(ContactConsent.EvidenceMaxLength).IsRequired();
        builder.Property(c => c.CollectedAt).HasColumnName("collected_at");
        builder.Property(c => c.RecordedAt).HasColumnName("recorded_at");
        builder.Property(c => c.ActorKind).HasColumnName("actor_kind").HasConversion<short>();
        builder.Property(c => c.ActorId).HasColumnName("actor_id");
        builder.Property(c => c.CustomerRef).HasColumnName("customer_ref").HasMaxLength(ContactExternalLink.CustomerRefMaxLength);
        builder.Property(c => c.SessionId).HasColumnName("session_id");
        builder.Property(c => c.SourceMessageId).HasColumnName("source_message_id");

        builder.HasIndex(c => new { c.TenantId, c.OrganizationId, c.Phone, c.Purpose, c.Seq }).HasDatabaseName(LatestIndex);
        builder.HasIndex(c => new { c.TenantId, c.SourceMessageId, c.Purpose }).IsUnique().HasFilter(KeywordFilter).HasDatabaseName(KeywordUniqueIndex);
    }
}

/// <summary>
/// A ocorrência de possível saída do cliente (ADR 0041, seção 5; E2.4). Uma por mensagem (índice único); resolvida UMA ÚNICA VEZ: o gatilho do banco recusa qualquer UPDATE de linha já resolvida e qualquer DELETE (migration
/// <c>OptOutReviews</c>), como defesa adicional ao aplicativo. Sem chave estrangeira: sessão e mensagem são referências de rastreio.
/// </summary>
internal sealed class OptOutReviewConfiguration : IEntityTypeConfiguration<OptOutReview>
{
    public const string ImmutableAfterResolutionTrigger = "tr_optout_reviews_resolve_once";

    public const string ImmutableAfterResolutionFunction = "optout_reviews_resolve_once";

    public const string MessageUniqueIndex = "ux_optout_reviews_message";

    public const string BySessionIndex = "ix_optout_reviews_tenant_id_session_id_opened_at";

    public void Configure(EntityTypeBuilder<OptOutReview> builder)
    {
        builder.ToTable("optout_reviews", table =>
        {
            table.HasCheckConstraint("ck_optout_reviews_reason", "reason IN (1, 2, 3)");
            table.HasCheckConstraint("ck_optout_reviews_keyword", "keyword IS NULL OR char_length(keyword) BETWEEN 1 AND 32");

            // A resolução é tudo ou nada: decisão, instante e responsável andam juntos.
            table.HasCheckConstraint(
                "ck_optout_reviews_resolution",
                "(resolution IS NULL AND resolved_at IS NULL AND resolved_by IS NULL) OR (resolution IN (1, 2) AND resolved_at IS NOT NULL AND resolved_by IS NOT NULL)");
        });

        builder.HasKey(r => new { r.TenantId, r.Id });
        builder.Property(r => r.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(r => r.OrganizationId).HasColumnName("organization_id");
        builder.Property(r => r.SessionId).HasColumnName("session_id");
        builder.Property(r => r.MessageId).HasColumnName("message_id");
        builder.Property(r => r.Reason).HasColumnName("reason").HasConversion<short>();
        builder.Property(r => r.Keyword).HasColumnName("keyword").HasMaxLength(OptOutReview.KeywordMaxLength);
        builder.Property(r => r.OpenedAt).HasColumnName("opened_at");
        builder.Property(r => r.Resolution).HasColumnName("resolution").HasConversion<short?>();
        builder.Property(r => r.ResolvedAt).HasColumnName("resolved_at");
        builder.Property(r => r.ResolvedBy).HasColumnName("resolved_by");
        builder.Ignore(r => r.IsResolved);

        builder.HasIndex(r => new { r.TenantId, r.MessageId }).IsUnique().HasDatabaseName(MessageUniqueIndex);
        builder.HasIndex(r => new { r.TenantId, r.SessionId, r.OpenedAt }).HasDatabaseName(BySessionIndex);
    }
}
