using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.Capabilities.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CapabilitiesInitial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "capabilities");

            migrationBuilder.CreateTable(
                name: "capability_definitions",
                schema: "capabilities",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    effect = table.Column<short>(type: "smallint", nullable: false),
                    requires_confirmation = table.Column<bool>(type: "boolean", nullable: false),
                    allowed_callers = table.Column<int>(type: "integer", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    spec_json = table.Column<string>(type: "character varying(16000)", maxLength: 16000, nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    published_by = table.Column<Guid>(type: "uuid", nullable: true),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_capability_definitions", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_capability_definitions_confirmation", "requires_confirmation = false OR effect = 2");
                    table.CheckConstraint("ck_capability_definitions_effect", "effect IN (1, 2)");
                    table.CheckConstraint("ck_capability_definitions_published", "(status = 1) = (published_at IS NULL)");
                    table.CheckConstraint("ck_capability_definitions_spec", "octet_length(spec_json) <= 16000");
                    table.CheckConstraint("ck_capability_definitions_status", "status IN (1, 2, 3)");
                    table.CheckConstraint("ck_capability_definitions_version", "version >= 1 AND revision >= 1");
                });

            migrationBuilder.CreateTable(
                name: "capability_executions",
                schema: "capabilities",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    definition_key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    definition_version = table.Column<int>(type: "integer", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    effect = table.Column<short>(type: "smallint", nullable: false),
                    caller = table.Column<int>(type: "integer", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: true),
                    service_session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    idempotency_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    error_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    upstream_status = table.Column<int>(type: "integer", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    duration_ms = table.Column<long>(type: "bigint", nullable: true),
                    is_test = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    confirmed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    confirmation_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    pending_ciphertext = table.Column<string>(type: "character varying(12000)", maxLength: 12000, nullable: true),
                    pending_key_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_capability_executions", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_capability_executions_effect", "effect IN (1, 2)");
                    table.CheckConstraint("ck_capability_executions_pending", "(pending_ciphertext IS NULL) = (pending_key_id IS NULL) AND (pending_ciphertext IS NULL OR status = 4)");
                    table.CheckConstraint("ck_capability_executions_revision", "revision >= 1 AND attempts >= 0");
                    table.CheckConstraint("ck_capability_executions_status", "status IN (1, 2, 3, 4, 5, 6, 7)");
                    table.CheckConstraint("ck_capability_executions_write_key", "effect = 1 OR is_test = true OR idempotency_key IS NOT NULL");
                });

            migrationBuilder.CreateTable(
                name: "integration_connections",
                schema: "capabilities",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    base_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    auth_type = table.Column<short>(type: "smallint", nullable: false),
                    auth_settings_json = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    default_headers_json = table.Column<string>(type: "character varying(12000)", maxLength: 12000, nullable: false),
                    allowed_headers_json = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    health_path = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    timeout_seconds = table.Column<int>(type: "integer", nullable: false),
                    max_response_bytes = table.Column<int>(type: "integer", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    secret_ciphertext = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    secret_key_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    secret_version = table.Column<int>(type: "integer", nullable: false),
                    secret_updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_integration_connections", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_integration_connections_auth", "auth_type IN (0, 1, 2, 3)");
                    table.CheckConstraint("ck_integration_connections_limits", "timeout_seconds >= 1 AND max_response_bytes >= 1024 AND revision >= 1 AND secret_version >= 0");
                    table.CheckConstraint("ck_integration_connections_secret", "(secret_ciphertext IS NULL) = (secret_key_id IS NULL)");
                });

            migrationBuilder.CreateIndex(
                name: "ix_capability_definitions_tenant_id_connection_id",
                schema: "capabilities",
                table: "capability_definitions",
                columns: new[] { "tenant_id", "connection_id" });

            migrationBuilder.CreateIndex(
                name: "ux_capability_definitions_draft",
                schema: "capabilities",
                table: "capability_definitions",
                columns: new[] { "tenant_id", "key" },
                unique: true,
                filter: "status = 1");

            migrationBuilder.CreateIndex(
                name: "ux_capability_definitions_version",
                schema: "capabilities",
                table: "capability_definitions",
                columns: new[] { "tenant_id", "key", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_capability_executions_tenant_id_connection_id_created_at",
                schema: "capabilities",
                table: "capability_executions",
                columns: new[] { "tenant_id", "connection_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_capability_executions_tenant_id_created_at",
                schema: "capabilities",
                table: "capability_executions",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_capability_executions_tenant_id_definition_key_created_at",
                schema: "capabilities",
                table: "capability_executions",
                columns: new[] { "tenant_id", "definition_key", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_capability_executions_idempotency",
                schema: "capabilities",
                table: "capability_executions",
                columns: new[] { "tenant_id", "idempotency_key" },
                unique: true,
                filter: "idempotency_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_integration_connections_key",
                schema: "capabilities",
                table: "integration_connections",
                columns: new[] { "tenant_id", "key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "capability_definitions",
                schema: "capabilities");

            migrationBuilder.DropTable(
                name: "capability_executions",
                schema: "capabilities");

            migrationBuilder.DropTable(
                name: "integration_connections",
                schema: "capabilities");
        }
    }
}
