using IaBimeta.AiGateway.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IaBimeta.AiGateway.Infrastructure.Configurations;

// Regra de ouro dos CHECKs deste esquema: um CHECK só recusa FALSE, e um resultado NULL PASSA. Por isso toda restrição abaixo trata cada coluna anulável de forma explícita
// (IS NULL / IS NOT NULL), e as colunas obrigatórias são NOT NULL.

internal sealed class AiTenantSettingsConfiguration : IEntityTypeConfiguration<AiTenantSettings>
{
    public const string OnePerTenantIndex = "ux_ai_tenant_settings_tenant";

    public void Configure(EntityTypeBuilder<AiTenantSettings> builder)
    {
        builder.ToTable("ai_tenant_settings", table =>
        {
            // Só a chave do próprio tenant (BYOK) existe: PlatformManaged é reservada e recusada também no banco.
            table.HasCheckConstraint("ck_ai_tenant_settings_source", "credential_source = 1");
            table.HasCheckConstraint("ck_ai_tenant_settings_status", "status IN (1, 2, 3)");
            table.HasCheckConstraint("ck_ai_tenant_settings_version", "version >= 1");

            // Cada sobrescrita de limite, quando existe, é positiva.
            table.HasCheckConstraint(
                "ck_ai_tenant_settings_limits",
                "(max_concurrent_calls IS NULL OR max_concurrent_calls > 0) AND (max_calls_per_period IS NULL OR max_calls_per_period > 0) " +
                "AND (max_tokens_per_period IS NULL OR max_tokens_per_period > 0) AND (period_seconds IS NULL OR period_seconds > 0) " +
                "AND (max_input_messages IS NULL OR max_input_messages > 0) AND (max_output_tokens IS NULL OR max_output_tokens > 0) " +
                "AND (max_tokens_per_call IS NULL OR max_tokens_per_call > 0)");
        });

        // TEN-3: a chave composta começa por tenant_id. O filtro global por tenant é aplicado no DbContext.
        builder.HasKey(s => new { s.TenantId, s.Id });
        builder.Property(s => s.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(s => s.ProviderId).HasColumnName("provider_id").HasMaxLength(50).IsRequired();
        builder.Property(s => s.ModelId).HasColumnName("model_id").HasMaxLength(100).IsRequired();
        builder.Property(s => s.CredentialSource).HasColumnName("credential_source").HasConversion<short>();
        builder.Property(s => s.Status).HasColumnName("status").HasConversion<short>();
        builder.Property(s => s.MaxConcurrentCalls).HasColumnName("max_concurrent_calls");
        builder.Property(s => s.MaxCallsPerPeriod).HasColumnName("max_calls_per_period");
        builder.Property(s => s.MaxTokensPerPeriod).HasColumnName("max_tokens_per_period");
        builder.Property(s => s.PeriodSeconds).HasColumnName("period_seconds");
        builder.Property(s => s.MaxInputMessages).HasColumnName("max_input_messages");
        builder.Property(s => s.MaxOutputTokens).HasColumnName("max_output_tokens");
        builder.Property(s => s.MaxTokensPerCall).HasColumnName("max_tokens_per_call");

        // A concorrência otimista: duas alterações do mesmo ponto de partida não se sobrescrevem em silêncio.
        builder.Property(s => s.Version).HasColumnName("version").IsConcurrencyToken();
        builder.Property(s => s.CreatedAt).HasColumnName("created_at");
        builder.Property(s => s.UpdatedAt).HasColumnName("updated_at");
        builder.Property(s => s.UpdatedBy).HasColumnName("updated_by");
        builder.Ignore(s => s.LimitOverrides);

        // Uma configuração por tenant.
        builder.HasIndex(s => s.TenantId).IsUnique().HasDatabaseName(OnePerTenantIndex);
    }
}

internal sealed class StoredAiCredentialConfiguration : IEntityTypeConfiguration<StoredAiCredential>
{
    public const string ActiveIndex = "ux_ai_credentials_active";

    public void Configure(EntityTypeBuilder<StoredAiCredential> builder)
    {
        builder.ToTable("ai_credentials", table =>
        {
            table.HasCheckConstraint("ck_ai_credentials_status", "status IN (1, 2)");
            table.HasCheckConstraint("ck_ai_credentials_version", "version >= 1");
            table.HasCheckConstraint("ck_ai_credentials_fingerprint", "length(fingerprint) = 12");

            // Ativa: tem a chave e o texto cifrado e não tem data de revogação. Revogada: o texto cifrado e a chave foram APAGADOS e há a data da revogação.
            table.HasCheckConstraint(
                "ck_ai_credentials_shape",
                "(status = 1 AND ciphertext IS NOT NULL AND key_id IS NOT NULL AND revoked_at IS NULL) OR (status = 2 AND ciphertext IS NULL AND key_id IS NULL AND revoked_at IS NOT NULL)");
        });

        builder.HasKey(c => new { c.TenantId, c.Id });
        builder.Property(c => c.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(c => c.ProviderId).HasColumnName("provider_id").HasMaxLength(50).IsRequired();
        builder.Property(c => c.KeyId).HasColumnName("key_id").HasMaxLength(64);
        builder.Property(c => c.Ciphertext).HasColumnName("ciphertext").HasMaxLength(16_384);
        builder.Property(c => c.Fingerprint).HasColumnName("fingerprint").HasMaxLength(12).IsRequired();
        builder.Property(c => c.Status).HasColumnName("status").HasConversion<short>();
        builder.Property(c => c.Version).HasColumnName("version").IsConcurrencyToken();
        builder.Property(c => c.CreatedAt).HasColumnName("created_at");
        builder.Property(c => c.UpdatedAt).HasColumnName("updated_at");
        builder.Property(c => c.UpdatedBy).HasColumnName("updated_by");
        builder.Property(c => c.RevokedAt).HasColumnName("revoked_at");
        builder.Ignore(c => c.IsActive);

        // No máximo uma credencial ATIVA por (tenant, provedor).
        builder.HasIndex(c => new { c.TenantId, c.ProviderId }).IsUnique().HasFilter("status = 1").HasDatabaseName(ActiveIndex);
    }
}

internal sealed class AiRequestConfiguration : IEntityTypeConfiguration<AiRequest>
{
    public const string OpenIndex = "ix_ai_requests_open";

    public void Configure(EntityTypeBuilder<AiRequest> builder)
    {
        builder.ToTable("ai_requests", table =>
        {
            table.HasCheckConstraint("ck_ai_requests_state", "state IN (1, 2, 3)");

            // O estado e o que está aplicado à janela andam juntos: reservada não aplica nada e não tem conclusão; confirmada e indeterminada aplicam o seu tipo e têm conclusão.
            table.HasCheckConstraint(
                "ck_ai_requests_applied",
                "(state = 1 AND applied_kind = 0 AND completed_at IS NULL) OR (state = 2 AND applied_kind = 2 AND completed_at IS NOT NULL) OR (state = 3 AND applied_kind = 3 AND completed_at IS NOT NULL)");
            table.HasCheckConstraint(
                "ck_ai_requests_applied_amounts",
                "(applied_kind = 0 AND applied_tokens = 0 AND applied_overage = 0) OR applied_kind = 2 OR (applied_kind = 3 AND applied_overage = 0)");
            table.HasCheckConstraint(
                "ck_ai_requests_tokens",
                "reserved_tokens >= 0 AND estimated_input_tokens >= 0 AND input_tokens >= 0 AND cached_input_tokens >= 0 AND cache_write_tokens >= 0 AND output_tokens >= 0 " +
                "AND applied_tokens >= 0 AND applied_overage >= 0 AND (reasoning_tokens IS NULL OR reasoning_tokens >= 0)");
            table.HasCheckConstraint("ck_ai_requests_cache", "cached_input_tokens + cache_write_tokens <= input_tokens");
            table.HasCheckConstraint("ck_ai_requests_lease", "lease_expires_at >= started_at");
            table.HasCheckConstraint("ck_ai_requests_outcome", "(outcome IS NULL OR outcome IN (1, 2, 3, 4)) AND (usage_certainty IS NULL OR usage_certainty IN (1, 2, 3))");
        });

        // TEN-3: a chave composta começa por tenant_id.
        builder.HasKey(r => new { r.TenantId, r.AiRequestId });
        builder.Property(r => r.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(r => r.AiRequestId).HasColumnName("ai_request_id").ValueGeneratedNever();
        builder.Property(r => r.State).HasColumnName("state").HasConversion<short>();
        builder.Property(r => r.CorrelationId).HasColumnName("correlation_id").HasMaxLength(100);
        builder.Property(r => r.ProviderId).HasColumnName("provider_id").HasMaxLength(50).IsRequired();
        builder.Property(r => r.ModelId).HasColumnName("model_id").HasMaxLength(100).IsRequired();
        builder.Property(r => r.ReservedTokens).HasColumnName("reserved_tokens");
        builder.Property(r => r.EstimatedInputTokens).HasColumnName("estimated_input_tokens");
        builder.Property(r => r.WindowStart).HasColumnName("window_start");
        builder.Property(r => r.StartedAt).HasColumnName("started_at");
        builder.Property(r => r.LeaseExpiresAt).HasColumnName("lease_expires_at");
        builder.Property(r => r.CompletedAt).HasColumnName("completed_at");
        builder.Property(r => r.InputTokens).HasColumnName("input_tokens").HasDefaultValue(0);
        builder.Property(r => r.CachedInputTokens).HasColumnName("cached_input_tokens").HasDefaultValue(0);
        builder.Property(r => r.CacheWriteTokens).HasColumnName("cache_write_tokens").HasDefaultValue(0);
        builder.Property(r => r.OutputTokens).HasColumnName("output_tokens").HasDefaultValue(0);
        builder.Property(r => r.ReasoningTokens).HasColumnName("reasoning_tokens");
        builder.Property(r => r.AppliedKind).HasColumnName("applied_kind").HasDefaultValue((short)0);
        builder.Property(r => r.AppliedTokens).HasColumnName("applied_tokens").HasDefaultValue(0L);
        builder.Property(r => r.AppliedOverage).HasColumnName("applied_overage").HasDefaultValue(0L);
        builder.Property(r => r.EstimatedCost).HasColumnName("estimated_cost").HasPrecision(18, 8).HasDefaultValue(0m);
        builder.Property(r => r.CostCurrency).HasColumnName("cost_currency").HasMaxLength(3);
        builder.Property(r => r.LatencyMs).HasColumnName("latency_ms");
        builder.Property(r => r.Outcome).HasColumnName("outcome").HasConversion<short?>();
        builder.Property(r => r.StopReason).HasColumnName("stop_reason").HasConversion<short?>();
        builder.Property(r => r.ErrorKind).HasColumnName("error_kind").HasConversion<short?>();
        builder.Property(r => r.ErrorCode).HasColumnName("error_code").HasMaxLength(100);
        builder.Property(r => r.ProviderRequestId).HasColumnName("provider_request_id").HasMaxLength(200);
        builder.Property(r => r.UsageCertainty).HasColumnName("usage_certainty").HasConversion<short?>();

        // As vagas ABERTAS por tenant, na ordem de expiração: a contagem de simultaneidade e a varredura de expiradas leem só isto.
        builder.HasIndex(r => new { r.TenantId, r.LeaseExpiresAt }).HasFilter("state = 1").HasDatabaseName(OpenIndex);

        // Consulta de consumo por período e por correlação (a correlação liga ao atendimento: é a parte a pseudonimizar na retenção).
        builder.HasIndex(r => new { r.TenantId, r.WindowStart }).HasDatabaseName("ix_ai_requests_tenant_id_window_start");
        builder.HasIndex(r => new { r.TenantId, r.StartedAt }).HasDatabaseName("ix_ai_requests_tenant_id_started_at");
        builder.HasIndex(r => new { r.TenantId, r.CorrelationId }).HasFilter("correlation_id IS NOT NULL").HasDatabaseName("ix_ai_requests_tenant_id_correlation_id");
    }
}

internal sealed class AiUsageWindowConfiguration : IEntityTypeConfiguration<AiUsageWindow>
{
    public void Configure(EntityTypeBuilder<AiUsageWindow> builder)
    {
        builder.ToTable("usage_windows", table =>
        {
            table.HasCheckConstraint("ck_usage_windows_counters", "calls >= 0 AND confirmed_tokens >= 0 AND indeterminate_tokens >= 0 AND overage_tokens >= 0");
            table.HasCheckConstraint("ck_usage_windows_span", "window_end > window_start AND period_seconds > 0");
        });

        builder.HasKey(w => new { w.TenantId, w.WindowStart });
        builder.Property(w => w.TenantId).HasColumnName("tenant_id").HasConversion(new TenantIdConverter()).ValueGeneratedNever();
        builder.Property(w => w.WindowStart).HasColumnName("window_start").ValueGeneratedNever();
        builder.Property(w => w.WindowEnd).HasColumnName("window_end");
        builder.Property(w => w.PeriodSeconds).HasColumnName("period_seconds");
        builder.Property(w => w.Calls).HasColumnName("calls").HasDefaultValue(0);
        builder.Property(w => w.ConfirmedTokens).HasColumnName("confirmed_tokens").HasDefaultValue(0L);
        builder.Property(w => w.IndeterminateTokens).HasColumnName("indeterminate_tokens").HasDefaultValue(0L);
        builder.Property(w => w.OverageTokens).HasColumnName("overage_tokens").HasDefaultValue(0L);

        // A janela ativa de um tenant, por data de fim.
        builder.HasIndex(w => new { w.TenantId, w.WindowEnd }).HasDatabaseName("ix_usage_windows_tenant_id_window_end");
    }
}
