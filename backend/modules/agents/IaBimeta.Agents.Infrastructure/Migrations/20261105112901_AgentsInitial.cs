using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.Agents.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AgentsInitial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "agents");

            migrationBuilder.CreateSequence(
                name: "lane_token_seq",
                schema: "agents");

            migrationBuilder.CreateTable(
                name: "agent_definition_versions",
                schema: "agents",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    instructions = table.Column<string>(type: "text", nullable: false),
                    faq = table.Column<string>(type: "jsonb", nullable: false),
                    audio_informative_text = table.Column<string>(type: "text", nullable: true),
                    handoff_notice_text = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    published_by = table.Column<Guid>(type: "uuid", nullable: true),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_definition_versions", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_agent_definition_versions_counters", "number >= 1 AND revision >= 1");
                    table.CheckConstraint("ck_agent_definition_versions_instructions", "length(instructions) BETWEEN 1 AND 8000");
                    table.CheckConstraint("ck_agent_definition_versions_published", "(status = 1 AND published_at IS NULL) OR (status IN (2, 3) AND published_at IS NOT NULL)");
                    table.CheckConstraint("ck_agent_definition_versions_status", "status IN (1, 2, 3)");
                });

            migrationBuilder.CreateTable(
                name: "ai_turns",
                schema: "agents",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<short>(type: "smallint", nullable: false),
                    first_received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    fire_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    supersede_count = table.Column<int>(type: "integer", nullable: false),
                    input_pending = table.Column<bool>(type: "boolean", nullable: false),
                    lane_token = table.Column<long>(type: "bigint", nullable: true),
                    claimed_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    content_wait_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    handoff_reason = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    notice_done = table.Column<bool>(type: "boolean", nullable: false),
                    end_reason = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_turns", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_ai_turns_counters", "attempts >= 0 AND supersede_count >= 0 AND version >= 1");
                    table.CheckConstraint("ck_ai_turns_ended", "(state IN (4, 5, 6, 7, 8)) = (ended_at IS NOT NULL AND end_reason IS NOT NULL)");
                    table.CheckConstraint("ck_ai_turns_next_attempt", "next_attempt_at IS NULL OR state = 2");
                    table.CheckConstraint("ck_ai_turns_running", "(state = 3) = (lane_token IS NOT NULL AND claimed_by IS NOT NULL AND lease_expires_at IS NOT NULL)");
                    table.CheckConstraint("ck_ai_turns_state", "state IN (1, 2, 3, 4, 5, 6, 7, 8)");
                });

            migrationBuilder.CreateTable(
                name: "inbox_message",
                schema: "agents",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    consumer = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inbox_message", x => new { x.tenant_id, x.consumer, x.event_id });
                });

            migrationBuilder.CreateIndex(
                name: "ux_agent_definition_versions_draft",
                schema: "agents",
                table: "agent_definition_versions",
                column: "tenant_id",
                unique: true,
                filter: "status = 1");

            migrationBuilder.CreateIndex(
                name: "ux_agent_definition_versions_number",
                schema: "agents",
                table: "agent_definition_versions",
                columns: new[] { "tenant_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_agent_definition_versions_published",
                schema: "agents",
                table: "agent_definition_versions",
                column: "tenant_id",
                unique: true,
                filter: "status = 2");

            migrationBuilder.CreateIndex(
                name: "ix_ai_turns_open_state_fire_at",
                schema: "agents",
                table: "ai_turns",
                columns: new[] { "state", "fire_at" },
                filter: "state IN (1, 2, 3)");

            migrationBuilder.CreateIndex(
                name: "ix_ai_turns_tenant_id_created_at",
                schema: "agents",
                table: "ai_turns",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_turns_tenant_id_session_id_created_at",
                schema: "agents",
                table: "ai_turns",
                columns: new[] { "tenant_id", "session_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_ai_turns_open_per_session",
                schema: "agents",
                table: "ai_turns",
                columns: new[] { "tenant_id", "session_id" },
                unique: true,
                filter: "state IN (1, 2, 3)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Verificação ANTES de qualquer mudança: a reversão não destrói dado. As definições de agente dos tenants (instruções e FAQ) e o histórico dos turnos de IA não têm cópia em outro lugar.
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM agents.agent_definition_versions) OR EXISTS (SELECT 1 FROM agents.ai_turns) THEN
        RAISE EXCEPTION 'Reversão recusada: há definições de agente ou turnos de IA; remover o esquema agents os destruiria. Exporte e limpe esses dados de forma explícita antes.';
    END IF;
END
$$;");

            migrationBuilder.DropTable(
                name: "agent_definition_versions",
                schema: "agents");

            migrationBuilder.DropTable(
                name: "ai_turns",
                schema: "agents");

            migrationBuilder.DropTable(
                name: "inbox_message",
                schema: "agents");

            migrationBuilder.DropSequence(
                name: "lane_token_seq",
                schema: "agents");
        }
    }
}
