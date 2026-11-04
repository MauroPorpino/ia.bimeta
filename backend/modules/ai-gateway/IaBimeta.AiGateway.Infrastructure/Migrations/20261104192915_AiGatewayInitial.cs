using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.AiGateway.Infrastructure.Migrations
{
    /// <summary>
    /// Migração 15 do plano da Etapa 5 (C1.4 do MVP): o esquema NOVO <c>ai_gateway</c> do AiGateway (ADR 0017), sem dado a preservar no <c>Up</c> e sem tocar em nenhuma tabela de outro módulo.
    ///
    /// Tabelas (todas com <c>tenant_id</c> e chave composta começando por ele, TEN-3): <c>ai_tenant_settings</c> (a configuração do tenant: provedor, modelo, situação e sobrescritas de limite),
    /// <c>ai_credentials</c> (a credencial BYOK SEMPRE cifrada; a revogação apaga o texto cifrado), <c>ai_requests</c> (o ledger por chamada: reservada, confirmada ou indeterminada, sem prompt e sem
    /// resposta) e <c>usage_windows</c> (os contadores por janela: um cache transacional reconstruível a partir de <c>ai_requests</c>).
    ///
    /// Além das restrições de cada tabela, um GATILHO em <c>ai_requests</c> só deixa passar as transições do estado da chamada (1 para 2, 1 para 3 e 3 para 2) e impede mudar os campos
    /// imutáveis da chamada e o que já está aplicado à janela: o banco recusa a dupla contagem mesmo contra um código que erre. Todo CHECK trata o nulo de forma explícita (um CHECK só recusa FALSE).
    ///
    /// Plano de reversão (<c>Down</c>): antes de remover qualquer coisa, VERIFICA se isso destruiria dado (credenciais BYOK, configurações ou registros de consumo) e, havendo, ABORTA com mensagem
    /// clara, sem alterar nada: as chaves dos tenants e o histórico de consumo não têm cópia em outro lugar, e nunca são descartados em silêncio. Sem dados, remove o gatilho, a função e as tabelas.
    /// </summary>
    public partial class AiGatewayInitial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ai_gateway");

            migrationBuilder.CreateTable(
                name: "ai_credentials",
                schema: "ai_gateway",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    key_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ciphertext = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: true),
                    fingerprint = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_credentials", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_ai_credentials_fingerprint", "length(fingerprint) = 12");
                    table.CheckConstraint("ck_ai_credentials_shape", "(status = 1 AND ciphertext IS NOT NULL AND key_id IS NOT NULL AND revoked_at IS NULL) OR (status = 2 AND ciphertext IS NULL AND key_id IS NULL AND revoked_at IS NOT NULL)");
                    table.CheckConstraint("ck_ai_credentials_status", "status IN (1, 2)");
                    table.CheckConstraint("ck_ai_credentials_version", "version >= 1");
                });

            migrationBuilder.CreateTable(
                name: "ai_requests",
                schema: "ai_gateway",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ai_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<short>(type: "smallint", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    provider_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    model_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    reserved_tokens = table.Column<long>(type: "bigint", nullable: false),
                    estimated_input_tokens = table.Column<long>(type: "bigint", nullable: false),
                    window_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    input_tokens = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    cached_input_tokens = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    cache_write_tokens = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    output_tokens = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    reasoning_tokens = table.Column<int>(type: "integer", nullable: true),
                    applied_kind = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    applied_tokens = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    applied_overage = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    estimated_cost = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false, defaultValue: 0m),
                    cost_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    latency_ms = table.Column<int>(type: "integer", nullable: true),
                    outcome = table.Column<short>(type: "smallint", nullable: true),
                    stop_reason = table.Column<short>(type: "smallint", nullable: true),
                    error_kind = table.Column<short>(type: "smallint", nullable: true),
                    error_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    provider_request_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    usage_certainty = table.Column<short>(type: "smallint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_requests", x => new { x.tenant_id, x.ai_request_id });
                    table.CheckConstraint("ck_ai_requests_applied", "(state = 1 AND applied_kind = 0 AND completed_at IS NULL) OR (state = 2 AND applied_kind = 2 AND completed_at IS NOT NULL) OR (state = 3 AND applied_kind = 3 AND completed_at IS NOT NULL)");
                    table.CheckConstraint("ck_ai_requests_applied_amounts", "(applied_kind = 0 AND applied_tokens = 0 AND applied_overage = 0) OR applied_kind = 2 OR (applied_kind = 3 AND applied_overage = 0)");
                    table.CheckConstraint("ck_ai_requests_cache", "cached_input_tokens + cache_write_tokens <= input_tokens");
                    table.CheckConstraint("ck_ai_requests_lease", "lease_expires_at >= started_at");
                    table.CheckConstraint("ck_ai_requests_outcome", "(outcome IS NULL OR outcome IN (1, 2, 3, 4)) AND (usage_certainty IS NULL OR usage_certainty IN (1, 2, 3))");
                    table.CheckConstraint("ck_ai_requests_state", "state IN (1, 2, 3)");
                    table.CheckConstraint("ck_ai_requests_tokens", "reserved_tokens >= 0 AND estimated_input_tokens >= 0 AND input_tokens >= 0 AND cached_input_tokens >= 0 AND cache_write_tokens >= 0 AND output_tokens >= 0 AND applied_tokens >= 0 AND applied_overage >= 0 AND (reasoning_tokens IS NULL OR reasoning_tokens >= 0)");
                });

            migrationBuilder.CreateTable(
                name: "ai_tenant_settings",
                schema: "ai_gateway",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    model_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    credential_source = table.Column<short>(type: "smallint", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    max_concurrent_calls = table.Column<int>(type: "integer", nullable: true),
                    max_calls_per_period = table.Column<int>(type: "integer", nullable: true),
                    max_tokens_per_period = table.Column<long>(type: "bigint", nullable: true),
                    period_seconds = table.Column<int>(type: "integer", nullable: true),
                    max_input_messages = table.Column<int>(type: "integer", nullable: true),
                    max_output_tokens = table.Column<int>(type: "integer", nullable: true),
                    max_tokens_per_call = table.Column<long>(type: "bigint", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_tenant_settings", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_ai_tenant_settings_limits", "(max_concurrent_calls IS NULL OR max_concurrent_calls > 0) AND (max_calls_per_period IS NULL OR max_calls_per_period > 0) AND (max_tokens_per_period IS NULL OR max_tokens_per_period > 0) AND (period_seconds IS NULL OR period_seconds > 0) AND (max_input_messages IS NULL OR max_input_messages > 0) AND (max_output_tokens IS NULL OR max_output_tokens > 0) AND (max_tokens_per_call IS NULL OR max_tokens_per_call > 0)");
                    table.CheckConstraint("ck_ai_tenant_settings_source", "credential_source = 1");
                    table.CheckConstraint("ck_ai_tenant_settings_status", "status IN (1, 2, 3)");
                    table.CheckConstraint("ck_ai_tenant_settings_version", "version >= 1");
                });

            migrationBuilder.CreateTable(
                name: "usage_windows",
                schema: "ai_gateway",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    window_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    window_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    period_seconds = table.Column<int>(type: "integer", nullable: false),
                    calls = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    confirmed_tokens = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    indeterminate_tokens = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    overage_tokens = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_usage_windows", x => new { x.tenant_id, x.window_start });
                    table.CheckConstraint("ck_usage_windows_counters", "calls >= 0 AND confirmed_tokens >= 0 AND indeterminate_tokens >= 0 AND overage_tokens >= 0");
                    table.CheckConstraint("ck_usage_windows_span", "window_end > window_start AND period_seconds > 0");
                });

            migrationBuilder.CreateIndex(
                name: "ux_ai_credentials_active",
                schema: "ai_gateway",
                table: "ai_credentials",
                columns: new[] { "tenant_id", "provider_id" },
                unique: true,
                filter: "status = 1");

            migrationBuilder.CreateIndex(
                name: "ix_ai_requests_open",
                schema: "ai_gateway",
                table: "ai_requests",
                columns: new[] { "tenant_id", "lease_expires_at" },
                filter: "state = 1");

            migrationBuilder.CreateIndex(
                name: "ix_ai_requests_tenant_id_correlation_id",
                schema: "ai_gateway",
                table: "ai_requests",
                columns: new[] { "tenant_id", "correlation_id" },
                filter: "correlation_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_ai_requests_tenant_id_started_at",
                schema: "ai_gateway",
                table: "ai_requests",
                columns: new[] { "tenant_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_requests_tenant_id_window_start",
                schema: "ai_gateway",
                table: "ai_requests",
                columns: new[] { "tenant_id", "window_start" });

            migrationBuilder.CreateIndex(
                name: "ux_ai_tenant_settings_tenant",
                schema: "ai_gateway",
                table: "ai_tenant_settings",
                column: "tenant_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_usage_windows_tenant_id_window_end",
                schema: "ai_gateway",
                table: "usage_windows",
                columns: new[] { "tenant_id", "window_end" });

            // O guarda do ledger: transição de estado inválida ou mudança de campo imutável é recusada pelo banco (SQLSTATE 23514, check_violation).
            migrationBuilder.Sql(@"
CREATE FUNCTION ai_gateway.ai_requests_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.tenant_id <> OLD.tenant_id OR NEW.ai_request_id <> OLD.ai_request_id OR NEW.reserved_tokens <> OLD.reserved_tokens
       OR NEW.estimated_input_tokens <> OLD.estimated_input_tokens OR NEW.window_start <> OLD.window_start OR NEW.started_at <> OLD.started_at THEN
        RAISE EXCEPTION 'ai_requests: os campos imutaveis da chamada nao podem mudar' USING ERRCODE = '23514';
    END IF;

    IF NEW.state = OLD.state THEN
        -- Mesmo estado: o que esta aplicado a janela e a conclusao nao mudam (so dado informativo, como a correlacao, pode ser alterado).
        IF NEW.applied_kind <> OLD.applied_kind OR NEW.applied_tokens <> OLD.applied_tokens OR NEW.applied_overage <> OLD.applied_overage
           OR NEW.completed_at IS DISTINCT FROM OLD.completed_at THEN
            RAISE EXCEPTION 'ai_requests: o que ja esta aplicado a janela nao muda sem uma transicao de estado' USING ERRCODE = '23514';
        END IF;

        RETURN NEW;
    END IF;

    IF NOT ((OLD.state = 1 AND NEW.state IN (2, 3)) OR (OLD.state = 3 AND NEW.state = 2)) THEN
        RAISE EXCEPTION 'ai_requests: transicao de estado invalida (% para %)', OLD.state, NEW.state USING ERRCODE = '23514';
    END IF;

    RETURN NEW;
END
$$;");
            migrationBuilder.Sql(@"
CREATE TRIGGER ai_requests_guard BEFORE UPDATE ON ai_gateway.ai_requests
    FOR EACH ROW EXECUTE FUNCTION ai_gateway.ai_requests_guard();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Verificação ANTES de qualquer mudança: a reversão não destrói dado. Credenciais BYOK, configurações e o histórico de consumo não têm cópia em outro lugar.
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM ai_gateway.ai_credentials) OR EXISTS (SELECT 1 FROM ai_gateway.ai_tenant_settings) OR EXISTS (SELECT 1 FROM ai_gateway.ai_requests) THEN
        RAISE EXCEPTION 'Reversão recusada: há credenciais, configurações ou registros de consumo de IA; remover o esquema ai_gateway os destruiria. Exporte e limpe esses dados de forma explícita antes.';
    END IF;
END
$$;");

            migrationBuilder.Sql("DROP TRIGGER IF EXISTS ai_requests_guard ON ai_gateway.ai_requests;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS ai_gateway.ai_requests_guard();");

            migrationBuilder.DropTable(
                name: "ai_credentials",
                schema: "ai_gateway");

            migrationBuilder.DropTable(
                name: "ai_requests",
                schema: "ai_gateway");

            migrationBuilder.DropTable(
                name: "ai_tenant_settings",
                schema: "ai_gateway");

            migrationBuilder.DropTable(
                name: "usage_windows",
                schema: "ai_gateway");
        }
    }
}
