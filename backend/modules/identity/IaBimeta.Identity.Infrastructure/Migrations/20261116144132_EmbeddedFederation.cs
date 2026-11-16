using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EmbeddedFederation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "auth_mode",
                schema: "identity",
                table: "users",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Password");

            migrationBuilder.AddColumn<string>(
                name: "external_user_id",
                schema: "identity",
                table: "sessions",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "integration_id",
                schema: "identity",
                table: "sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "kind",
                schema: "identity",
                table: "sessions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Interactive");

            migrationBuilder.AddColumn<string>(
                name: "parent_origin",
                schema: "identity",
                table: "sessions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "integration_connections",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    provisioning = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    allowed_origins = table.Column<string[]>(type: "text[]", nullable: false),
                    allow_start_conversation = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_integration_connections", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_integration_connections_mode", "mode IN ('FullAttendant','CustomerOnly')");
                    table.CheckConstraint("ck_integration_connections_origins", "cardinality(allowed_origins) BETWEEN 1 AND 10");
                    table.CheckConstraint("ck_integration_connections_provisioning", "provisioning IN ('MappedOnly','JitAttendant')");
                    table.CheckConstraint("ck_integration_connections_status", "status IN ('Active','Disabled')");
                });

            migrationBuilder.CreateTable(
                name: "integration_nonces",
                schema: "identity",
                columns: table => new
                {
                    key_id = table.Column<Guid>(type: "uuid", nullable: false),
                    nonce = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_integration_nonces", x => new { x.key_id, x.nonce });
                });

            migrationBuilder.CreateTable(
                name: "session_customer_grants",
                schema: "identity",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_ref = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    granted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_customer_grants", x => new { x.tenant_id, x.session_id, x.customer_ref });
                    table.ForeignKey(
                        name: "FK_session_customer_grants_sessions_tenant_id_session_id",
                        columns: x => new { x.tenant_id, x.session_id },
                        principalSchema: "identity",
                        principalTable: "sessions",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "external_identities",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_user_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_external_identities", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_external_identities_source", "source IN ('Mapped','Jit')");
                    table.CheckConstraint("ck_external_identities_status", "status IN ('Active','Disabled')");
                    table.ForeignKey(
                        name: "FK_external_identities_integration_connections_tenant_id_conne~",
                        columns: x => new { x.tenant_id, x.connection_id },
                        principalSchema: "identity",
                        principalTable: "integration_connections",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_external_identities_users_tenant_id_user_id",
                        columns: x => new { x.tenant_id, x.user_id },
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "integration_keys",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    secret_key_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    secret_ciphertext = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_integration_keys", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_integration_keys_status", "status IN ('Active','Revoked')");
                    table.ForeignKey(
                        name: "FK_integration_keys_integration_connections_tenant_id_connecti~",
                        columns: x => new { x.tenant_id, x.connection_id },
                        principalSchema: "identity",
                        principalTable: "integration_connections",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "launch_tickets",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    challenge = table.Column<string>(type: "character varying(43)", maxLength: 43, nullable: true),
                    parent_origin = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    external_user_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_ref = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_launch_tickets", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_launch_tickets_kind", "kind IN ('Launch','Context')");
                    table.ForeignKey(
                        name: "FK_launch_tickets_integration_connections_tenant_id_connection~",
                        columns: x => new { x.tenant_id, x.connection_id },
                        principalSchema: "identity",
                        principalTable: "integration_connections",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sessions_tenant_id_integration_id_external_user_id",
                schema: "identity",
                table: "sessions",
                columns: new[] { "tenant_id", "integration_id", "external_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_external_identities_tenant_id_connection_id_external_user_id",
                schema: "identity",
                table: "external_identities",
                columns: new[] { "tenant_id", "connection_id", "external_user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_external_identities_tenant_id_connection_id_user_id",
                schema: "identity",
                table: "external_identities",
                columns: new[] { "tenant_id", "connection_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_external_identities_tenant_id_user_id",
                schema: "identity",
                table: "external_identities",
                columns: new[] { "tenant_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_integration_connections_tenant_id_organization_id",
                schema: "identity",
                table: "integration_connections",
                columns: new[] { "tenant_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_integration_keys_id",
                schema: "identity",
                table: "integration_keys",
                column: "id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_integration_keys_tenant_id_connection_id",
                schema: "identity",
                table: "integration_keys",
                columns: new[] { "tenant_id", "connection_id" });

            migrationBuilder.CreateIndex(
                name: "ix_integration_nonces_expires_at",
                schema: "identity",
                table: "integration_nonces",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_launch_tickets_expires_at",
                schema: "identity",
                table: "launch_tickets",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_launch_tickets_tenant_id_connection_id",
                schema: "identity",
                table: "launch_tickets",
                columns: new[] { "tenant_id", "connection_id" });

            migrationBuilder.CreateIndex(
                name: "ix_launch_tickets_token_hash",
                schema: "identity",
                table: "launch_tickets",
                column: "token_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "external_identities",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "integration_keys",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "integration_nonces",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "launch_tickets",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "session_customer_grants",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "integration_connections",
                schema: "identity");

            migrationBuilder.DropIndex(
                name: "ix_sessions_tenant_id_integration_id_external_user_id",
                schema: "identity",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "auth_mode",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "external_user_id",
                schema: "identity",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "integration_id",
                schema: "identity",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "kind",
                schema: "identity",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "parent_origin",
                schema: "identity",
                table: "sessions");
        }
    }
}
