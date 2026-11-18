using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IaBimeta.Identity.Infrastructure.Configurations;

/// <summary>O nonce de uma chamada assinada já usada (replay). Só identificadores e o instante em que pode ser esquecido; interno ao módulo.</summary>
internal sealed class IntegrationNonce : ITenantOwned
{
    public IntegrationNonce(TenantId tenantId, Guid keyId, string nonce, DateTimeOffset expiresAt)
    {
        TenantId = tenantId;
        KeyId = keyId;
        Nonce = nonce;
        ExpiresAt = expiresAt;
    }

    public TenantId TenantId { get; }

    public Guid KeyId { get; }

    public string Nonce { get; }

    public DateTimeOffset ExpiresAt { get; }
}

internal sealed class IntegrationConnectionConfiguration : IEntityTypeConfiguration<IntegrationConnection>
{
    public void Configure(EntityTypeBuilder<IntegrationConnection> builder)
    {
        builder.ToTable("integration_connections", table =>
        {
            table.HasCheckConstraint("ck_integration_connections_origins", "cardinality(allowed_origins) BETWEEN 1 AND 10");
            table.HasCheckConstraint("ck_integration_connections_mode", "mode IN ('FullAttendant','CustomerOnly')");
            table.HasCheckConstraint("ck_integration_connections_provisioning", "provisioning IN ('MappedOnly','JitAttendant')");
            table.HasCheckConstraint("ck_integration_connections_status", "status IN ('Active','Disabled')");
        });

        builder.HasKey(c => new { c.TenantId, c.Id });
        builder.Property(c => c.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(c => c.OrganizationId).HasColumnName("organization_id");
        builder.Property(c => c.Name).HasColumnName("name").HasMaxLength(IntegrationConnection.NameMaxLength).IsRequired();
        builder.Property(c => c.Mode).HasColumnName("mode").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(c => c.Provisioning).HasColumnName("provisioning").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(c => c.AllowedOrigins).HasColumnName("allowed_origins").IsRequired();
        builder.Property(c => c.AllowStartConversation).HasColumnName("allow_start_conversation");
        builder.Property(c => c.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(c => c.CreatedAt).HasColumnName("created_at");
        builder.Property(c => c.CreatedByUserId).HasColumnName("created_by_user_id");
        builder.Property(c => c.UpdatedAt).HasColumnName("updated_at");
        builder.Property(c => c.SessionsRevokedAt).HasColumnName("sessions_revoked_at");

        // A organização pertence a outro esquema (Tenancy): sem chave estrangeira entre esquemas (DAD-2); o caso de uso valida.
        builder.HasIndex(c => new { c.TenantId, c.OrganizationId }).HasDatabaseName("ix_integration_connections_tenant_id_organization_id");
    }
}

internal sealed class IntegrationKeyConfiguration : IEntityTypeConfiguration<IntegrationKey>
{
    public void Configure(EntityTypeBuilder<IntegrationKey> builder)
    {
        builder.ToTable("integration_keys", table => table.HasCheckConstraint("ck_integration_keys_status", "status IN ('Active','Revoked')"));

        builder.HasKey(k => new { k.TenantId, k.Id });
        builder.Property(k => k.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(k => k.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(k => k.ConnectionId).HasColumnName("connection_id");
        builder.Property(k => k.SecretKeyId).HasColumnName("secret_key_id").HasMaxLength(64).IsRequired();
        builder.Property(k => k.SecretCiphertext).HasColumnName("secret_ciphertext").HasMaxLength(512).IsRequired();
        builder.Property(k => k.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(k => k.CreatedAt).HasColumnName("created_at");
        builder.Property(k => k.ExpiresAt).HasColumnName("expires_at");
        builder.Property(k => k.RevokedAt).HasColumnName("revoked_at");
        builder.Property(k => k.CreatedByUserId).HasColumnName("created_by_user_id");
        builder.Ignore(k => k.PublicId);

        builder.HasOne<IntegrationConnection>().WithMany().HasForeignKey(k => new { k.TenantId, k.ConnectionId }).OnDelete(DeleteBehavior.Cascade);

        // A chamada assinada traz o identificador público da chave ANTES de existir tenant: o identificador é único na tabela inteira.
        builder.HasIndex(k => k.Id).IsUnique().HasDatabaseName("ix_integration_keys_id");
        builder.HasIndex(k => new { k.TenantId, k.ConnectionId }).HasDatabaseName("ix_integration_keys_tenant_id_connection_id");
    }
}

internal sealed class IntegrationNonceConfiguration : IEntityTypeConfiguration<IntegrationNonce>
{
    public void Configure(EntityTypeBuilder<IntegrationNonce> builder)
    {
        builder.ToTable("integration_nonces");

        // O nonce é por chave: a unicidade (key_id, nonce) é a defesa contra replay.
        builder.HasKey(n => new { n.KeyId, n.Nonce });
        builder.Property(n => n.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter());
        builder.Property(n => n.KeyId).HasColumnName("key_id");
        builder.Property(n => n.Nonce).HasColumnName("nonce").HasMaxLength(64).IsRequired();
        builder.Property(n => n.ExpiresAt).HasColumnName("expires_at");

        builder.HasIndex(n => n.ExpiresAt).HasDatabaseName("ix_integration_nonces_expires_at");
    }
}

internal sealed class LaunchTicketConfiguration : IEntityTypeConfiguration<LaunchTicket>
{
    public void Configure(EntityTypeBuilder<LaunchTicket> builder)
    {
        builder.ToTable("launch_tickets", table => table.HasCheckConstraint("ck_launch_tickets_kind", "kind IN ('Launch','Context')"));

        builder.HasKey(t => new { t.TenantId, t.Id });
        builder.Property(t => t.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(t => t.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(t => t.ConnectionId).HasColumnName("connection_id");
        builder.Property(t => t.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(t => t.TokenHash).HasColumnName("token_hash").HasMaxLength(LaunchTicket.TokenHashLength).IsRequired();
        builder.Property(t => t.Challenge).HasColumnName("challenge").HasMaxLength(LaunchTicket.ChallengeLength);
        builder.Property(t => t.ParentOrigin).HasColumnName("parent_origin").HasMaxLength(IntegrationConnection.OriginMaxLength);
        builder.Property(t => t.ExternalUserId).HasColumnName("external_user_id").HasMaxLength(ExternalIdentity.ExternalUserIdMaxLength).IsRequired();
        builder.Property(t => t.UserId).HasColumnName("user_id");
        builder.Property(t => t.CustomerRef).HasColumnName("customer_ref").HasMaxLength(LaunchTicket.CustomerRefMaxLength);
        builder.Property(t => t.CreatedAt).HasColumnName("created_at");
        builder.Property(t => t.ExpiresAt).HasColumnName("expires_at");
        builder.Property(t => t.UsedAt).HasColumnName("used_at");
        builder.Property(t => t.KeyId).HasColumnName("key_id");
        builder.Property(t => t.SessionId).HasColumnName("session_id");

        builder.HasOne<IntegrationConnection>().WithMany().HasForeignKey(t => new { t.TenantId, t.ConnectionId }).OnDelete(DeleteBehavior.Cascade);

        // O ticket apresentado é achado pelo hash (único na tabela inteira); o tenant é o da linha encontrada.
        builder.HasIndex(t => t.TokenHash).IsUnique().HasDatabaseName("ix_launch_tickets_token_hash");
        builder.HasIndex(t => t.ExpiresAt).HasDatabaseName("ix_launch_tickets_expires_at");
    }
}

internal sealed class ExternalIdentityConfiguration : IEntityTypeConfiguration<ExternalIdentity>
{
    public const string UniqueExternalUserIndex = "ix_external_identities_tenant_id_connection_id_external_user_id";
    public const string UniqueUserIndex = "ix_external_identities_tenant_id_connection_id_user_id";

    public void Configure(EntityTypeBuilder<ExternalIdentity> builder)
    {
        builder.ToTable("external_identities", table =>
        {
            table.HasCheckConstraint("ck_external_identities_source", "source IN ('Mapped','Jit')");
            table.HasCheckConstraint("ck_external_identities_status", "status IN ('Active','Disabled')");
        });

        builder.HasKey(i => new { i.TenantId, i.Id });
        builder.Property(i => i.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(i => i.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(i => i.ConnectionId).HasColumnName("connection_id");
        builder.Property(i => i.ExternalUserId).HasColumnName("external_user_id").HasMaxLength(ExternalIdentity.ExternalUserIdMaxLength).IsRequired();
        builder.Property(i => i.UserId).HasColumnName("user_id");
        builder.Property(i => i.Source).HasColumnName("source").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(i => i.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(i => i.CreatedAt).HasColumnName("created_at");
        builder.Property(i => i.CreatedByUserId).HasColumnName("created_by_user_id");
        builder.Property(i => i.SessionsRevokedAt).HasColumnName("sessions_revoked_at");

        builder.HasOne<IntegrationConnection>().WithMany().HasForeignKey(i => new { i.TenantId, i.ConnectionId }).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(i => new { i.TenantId, i.UserId }).OnDelete(DeleteBehavior.Cascade);

        // Um vínculo por usuário externo e um por usuário interno, dentro da conexão.
        builder.HasIndex(i => new { i.TenantId, i.ConnectionId, i.ExternalUserId }).IsUnique().HasDatabaseName(UniqueExternalUserIndex);
        builder.HasIndex(i => new { i.TenantId, i.ConnectionId, i.UserId }).IsUnique().HasDatabaseName(UniqueUserIndex);
    }
}

internal sealed class SessionCustomerGrantConfiguration : IEntityTypeConfiguration<SessionCustomerGrant>
{
    public void Configure(EntityTypeBuilder<SessionCustomerGrant> builder)
    {
        builder.ToTable("session_customer_grants");

        builder.HasKey(g => new { g.TenantId, g.SessionId, g.CustomerRef });
        builder.Property(g => g.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(g => g.SessionId).HasColumnName("session_id");
        builder.Property(g => g.CustomerRef).HasColumnName("customer_ref").HasMaxLength(LaunchTicket.CustomerRefMaxLength).IsRequired();
        builder.Property(g => g.GrantedAt).HasColumnName("granted_at");

        // Morre com a sessão (o expurgo das sessões leva os clientes em foco junto).
        builder.HasOne<Session>().WithMany().HasForeignKey(g => new { g.TenantId, g.SessionId }).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PendingAuditConfiguration : IEntityTypeConfiguration<PendingAudit>
{
    public void Configure(EntityTypeBuilder<PendingAudit> builder)
    {
        builder.ToTable("audit_outbox", table => table.HasCheckConstraint("ck_audit_outbox_outcome", "outcome > 0"));

        builder.HasKey(a => new { a.TenantId, a.Id });
        builder.Property(a => a.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(a => a.Operation).HasColumnName("operation").HasMaxLength(PendingAudit.OperationMaxLength).IsRequired();
        builder.Property(a => a.ResourceType).HasColumnName("resource_type").HasMaxLength(PendingAudit.ResourceTypeMaxLength).IsRequired();
        builder.Property(a => a.ResourceId).HasColumnName("resource_id").HasMaxLength(PendingAudit.ResourceIdMaxLength);
        builder.Property(a => a.Outcome).HasColumnName("outcome").HasConversion<short>();
        builder.Property(a => a.ActorUserId).HasColumnName("actor_user_id");
        builder.Property(a => a.ActorOrigin).HasColumnName("actor_origin").HasMaxLength(PendingAudit.ActorOriginMaxLength).IsRequired();
        builder.Property(a => a.CreatedAt).HasColumnName("created_at");
        builder.Property(a => a.Attempts).HasColumnName("attempts").HasDefaultValue(0);
        builder.Property(a => a.NextAttemptAt).HasColumnName("next_attempt_at");
        builder.Property(a => a.LastError).HasColumnName("last_error").HasMaxLength(PendingAudit.LastErrorMaxLength);

        // A entrega lê as mais antigas primeiro.
        builder.HasIndex(a => new { a.TenantId, a.CreatedAt, a.Id }).HasDatabaseName("ix_audit_outbox_tenant_id_created_at_id");
    }
}
