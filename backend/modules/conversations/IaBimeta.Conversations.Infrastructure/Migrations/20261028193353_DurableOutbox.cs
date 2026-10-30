using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace IaBimeta.Conversations.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DurableOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "outbox_message",
                schema: "conversations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordering_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ingest_seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    event_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    event_version = table.Column<int>(type: "integer", nullable: false),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    causation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    state = table.Column<short>(type: "smallint", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lease_owner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    lease_token = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    dead_reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_message", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_outbox_message_attempts", "attempts >= 0");
                    table.CheckConstraint("ck_outbox_message_lease", "(state = 2) = (lease_token IS NOT NULL)");
                    table.CheckConstraint("ck_outbox_message_state", "state IN (1, 2, 3, 4)");
                });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_message_state_next_attempt_at_ingest_seq",
                schema: "conversations",
                table: "outbox_message",
                columns: new[] { "state", "next_attempt_at", "ingest_seq" },
                filter: "state IN (1, 2)");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_message_tenant_id_ordering_key_ingest_seq",
                schema: "conversations",
                table: "outbox_message",
                columns: new[] { "tenant_id", "ordering_key", "ingest_seq" },
                filter: "state IN (1, 2)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbox_message",
                schema: "conversations");
        }
    }
}
