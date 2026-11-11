using IaBimeta.Capabilities.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IaBimeta.Capabilities.Infrastructure.Configurations;

/// <summary>As conexões com sistemas externos. O segredo é guardado cifrado (<c>secret_ciphertext</c>); uma chave por tenant.</summary>
internal sealed class IntegrationConnectionConfiguration : IEntityTypeConfiguration<IntegrationConnection>
{
    public const string KeyIndex = "ux_integration_connections_key";

    public void Configure(EntityTypeBuilder<IntegrationConnection> builder)
    {
        builder.ToTable("integration_connections", table =>
        {
            table.HasCheckConstraint("ck_integration_connections_auth", "auth_type IN (0, 1, 2, 3)");
            table.HasCheckConstraint("ck_integration_connections_limits", "timeout_seconds >= 1 AND max_response_bytes >= 1024 AND revision >= 1 AND secret_version >= 0");
            table.HasCheckConstraint("ck_integration_connections_secret", "(secret_ciphertext IS NULL) = (secret_key_id IS NULL)");
        });

        builder.HasKey(c => new { c.TenantId, c.Id });
        builder.Property(c => c.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(c => c.OrganizationId).HasColumnName("organization_id");
        builder.Property(c => c.Key).HasColumnName("key").HasMaxLength(40);
        builder.Property(c => c.Name).HasColumnName("name").HasMaxLength(100);
        builder.Property(c => c.Kind).HasColumnName("kind").HasMaxLength(40);
        builder.Property(c => c.BaseUrl).HasColumnName("base_url").HasMaxLength(500);
        builder.Property(c => c.AuthType).HasColumnName("auth_type").HasConversion<short>();
        builder.Property(c => c.AuthSettingsJson).HasColumnName("auth_settings_json").HasMaxLength(2000);
        builder.Property(c => c.DefaultHeadersJson).HasColumnName("default_headers_json").HasMaxLength(12000);
        builder.Property(c => c.AllowedHeadersJson).HasColumnName("allowed_headers_json").HasMaxLength(2000);
        builder.Property(c => c.HealthPath).HasColumnName("health_path").HasMaxLength(200);
        builder.Property(c => c.TimeoutSeconds).HasColumnName("timeout_seconds");
        builder.Property(c => c.MaxResponseBytes).HasColumnName("max_response_bytes");
        builder.Property(c => c.Enabled).HasColumnName("enabled");
        builder.Property(c => c.SecretCiphertext).HasColumnName("secret_ciphertext").HasMaxLength(8000);
        builder.Property(c => c.SecretKeyId).HasColumnName("secret_key_id").HasMaxLength(64);
        builder.Property(c => c.SecretVersion).HasColumnName("secret_version");
        builder.Property(c => c.SecretUpdatedAt).HasColumnName("secret_updated_at");
        builder.Property(c => c.CreatedBy).HasColumnName("created_by");
        builder.Property(c => c.CreatedAt).HasColumnName("created_at");
        builder.Property(c => c.UpdatedAt).HasColumnName("updated_at");
        builder.Property(c => c.Revision).HasColumnName("revision").IsConcurrencyToken();
        builder.Ignore(c => c.HasSecret);
        builder.Ignore(c => c.AuthSettings);
        builder.Ignore(c => c.DefaultHeaders);
        builder.Ignore(c => c.AllowedHeaders);

        builder.HasIndex(c => new { c.TenantId, c.Key }).IsUnique().HasDatabaseName(KeyIndex);
    }
}

/// <summary>As versões das operações. Por chave: no máximo UM rascunho e UMA versão publicada (índices únicos parciais).</summary>
internal sealed class CapabilityDefinitionConfiguration : IEntityTypeConfiguration<CapabilityDefinition>
{
    public const string PublishedIndex = "ux_capability_definitions_published";
    public const string DraftIndex = "ux_capability_definitions_draft";
    public const string VersionIndex = "ux_capability_definitions_version";

    public void Configure(EntityTypeBuilder<CapabilityDefinition> builder)
    {
        builder.ToTable("capability_definitions", table =>
        {
            table.HasCheckConstraint("ck_capability_definitions_status", "status IN (1, 2, 3)");
            table.HasCheckConstraint("ck_capability_definitions_effect", "effect IN (1, 2)");
            table.HasCheckConstraint("ck_capability_definitions_version", "version >= 1 AND revision >= 1");
            table.HasCheckConstraint("ck_capability_definitions_spec", "octet_length(spec_json) <= 16000");
            table.HasCheckConstraint("ck_capability_definitions_published", "(status = 1) = (published_at IS NULL)");
            table.HasCheckConstraint("ck_capability_definitions_confirmation", "requires_confirmation = false OR effect = 2");
        });

        builder.HasKey(d => new { d.TenantId, d.Id });
        builder.Property(d => d.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(d => d.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(d => d.ConnectionId).HasColumnName("connection_id");
        builder.Property(d => d.Key).HasColumnName("key").HasMaxLength(80);
        builder.Property(d => d.Version).HasColumnName("version");
        builder.Property(d => d.Status).HasColumnName("status").HasConversion<short>();
        builder.Property(d => d.Name).HasColumnName("name").HasMaxLength(100);
        builder.Property(d => d.Description).HasColumnName("description").HasMaxLength(500);
        builder.Property(d => d.Effect).HasColumnName("effect").HasConversion<short>();
        builder.Property(d => d.RequiresConfirmation).HasColumnName("requires_confirmation");
        builder.Property(d => d.AllowedCallers).HasColumnName("allowed_callers").HasConversion<int>();
        builder.Property(d => d.Enabled).HasColumnName("enabled");
        builder.Property(d => d.SpecJson).HasColumnName("spec_json").HasMaxLength(16000);
        builder.Property(d => d.CreatedBy).HasColumnName("created_by");
        builder.Property(d => d.CreatedAt).HasColumnName("created_at");
        builder.Property(d => d.UpdatedAt).HasColumnName("updated_at");
        builder.Property(d => d.PublishedAt).HasColumnName("published_at");
        builder.Property(d => d.PublishedBy).HasColumnName("published_by");
        builder.Property(d => d.Revision).HasColumnName("revision").IsConcurrencyToken();
        builder.Ignore(d => d.Spec);

        builder.HasIndex(d => new { d.TenantId, d.Key, d.Version }).IsUnique().HasDatabaseName(VersionIndex);
        builder.HasIndex(d => new { d.TenantId, d.Key }).IsUnique().HasFilter("status = 2").HasDatabaseName(PublishedIndex);
        // O índice do rascunho inclui o status nas colunas: sem isso o EF funde os dois índices parciais (mesmas colunas) e o da versão publicada se perde.
        builder.HasIndex(d => new { d.TenantId, d.Key, d.Status }).IsUnique().HasFilter("status = 1").HasDatabaseName(DraftIndex);
        builder.HasIndex(d => new { d.TenantId, d.ConnectionId }).HasDatabaseName("ix_capability_definitions_tenant_id_connection_id");
    }
}

/// <summary>O registro das execuções. Escrita tem chave de idempotência ÚNICA por tenant (a disputa entre chamadas simultâneas é decidida aqui). Nunca guarda parâmetros nem resposta.</summary>
internal sealed class CapabilityExecutionConfiguration : IEntityTypeConfiguration<CapabilityExecution>
{
    public const string IdempotencyIndex = "ux_capability_executions_idempotency";

    public void Configure(EntityTypeBuilder<CapabilityExecution> builder)
    {
        builder.ToTable("capability_executions", table =>
        {
            table.HasCheckConstraint("ck_capability_executions_status", "status IN (1, 2, 3, 4, 5, 6, 7)");
            table.HasCheckConstraint("ck_capability_executions_effect", "effect IN (1, 2)");
            table.HasCheckConstraint("ck_capability_executions_write_key", "effect = 1 OR is_test = true OR idempotency_key IS NOT NULL");
            table.HasCheckConstraint("ck_capability_executions_pending", "(pending_ciphertext IS NULL) = (pending_key_id IS NULL) AND (pending_ciphertext IS NULL OR status = 4)");
            table.HasCheckConstraint("ck_capability_executions_revision", "revision >= 1 AND attempts >= 0");
        });

        builder.HasKey(e => new { e.TenantId, e.Id });
        builder.Property(e => e.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(e => e.DefinitionId).HasColumnName("definition_id");
        builder.Property(e => e.DefinitionKey).HasColumnName("definition_key").HasMaxLength(80);
        builder.Property(e => e.DefinitionVersion).HasColumnName("definition_version");
        builder.Property(e => e.ConnectionId).HasColumnName("connection_id");
        builder.Property(e => e.OrganizationId).HasColumnName("organization_id");
        builder.Property(e => e.Effect).HasColumnName("effect").HasConversion<short>();
        builder.Property(e => e.Caller).HasColumnName("caller").HasConversion<int>();
        builder.Property(e => e.RequestedBy).HasColumnName("requested_by");
        builder.Property(e => e.ServiceSessionId).HasColumnName("service_session_id");
        builder.Property(e => e.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(100);
        builder.Property(e => e.Fingerprint).HasColumnName("fingerprint").HasMaxLength(64);
        builder.Property(e => e.Status).HasColumnName("status").HasConversion<short>();
        builder.Property(e => e.ErrorCode).HasColumnName("error_code").HasMaxLength(40);
        builder.Property(e => e.UpstreamStatus).HasColumnName("upstream_status");
        builder.Property(e => e.Attempts).HasColumnName("attempts");
        builder.Property(e => e.DurationMs).HasColumnName("duration_ms");
        builder.Property(e => e.IsTest).HasColumnName("is_test");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at");
        builder.Property(e => e.CompletedAt).HasColumnName("completed_at");
        builder.Property(e => e.ConfirmedBy).HasColumnName("confirmed_by");
        builder.Property(e => e.ConfirmedAt).HasColumnName("confirmed_at");
        builder.Property(e => e.ConfirmationExpiresAt).HasColumnName("confirmation_expires_at");
        builder.Property(e => e.PendingCiphertext).HasColumnName("pending_ciphertext").HasMaxLength(12000);
        builder.Property(e => e.PendingKeyId).HasColumnName("pending_key_id").HasMaxLength(64);
        builder.Property(e => e.Revision).HasColumnName("revision").IsConcurrencyToken();

        builder.HasIndex(e => new { e.TenantId, e.IdempotencyKey }).IsUnique().HasFilter("idempotency_key IS NOT NULL").HasDatabaseName(IdempotencyIndex);
        builder.HasIndex(e => new { e.TenantId, e.CreatedAt }).HasDatabaseName("ix_capability_executions_tenant_id_created_at");
        builder.HasIndex(e => new { e.TenantId, e.ConnectionId, e.CreatedAt }).HasDatabaseName("ix_capability_executions_tenant_id_connection_id_created_at");
        builder.HasIndex(e => new { e.TenantId, e.DefinitionKey, e.CreatedAt }).HasDatabaseName("ix_capability_executions_tenant_id_definition_key_created_at");
    }
}
