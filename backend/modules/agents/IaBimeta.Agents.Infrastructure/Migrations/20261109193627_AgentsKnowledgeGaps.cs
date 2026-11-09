using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.Agents.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AgentsKnowledgeGaps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "knowledge_gaps",
                schema: "agents",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    question_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    promoted_version = table.Column<int>(type: "integer", nullable: true),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_knowledge_gaps", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_knowledge_gaps_decided", "(status = 1) = (decided_at IS NULL)");
                    table.CheckConstraint("ck_knowledge_gaps_hash", "octet_length(question_hash) = 32");
                    table.CheckConstraint("ck_knowledge_gaps_kind", "kind IN (1, 2, 3)");
                    table.CheckConstraint("ck_knowledge_gaps_promoted_version", "promoted_version IS NULL OR (status = 3 AND promoted_version >= 1)");
                    table.CheckConstraint("ck_knowledge_gaps_revision", "revision >= 1");
                    table.CheckConstraint("ck_knowledge_gaps_status", "status IN (1, 2, 3)");
                });

            migrationBuilder.CreateTable(
                name: "knowledge_gap_occurrences",
                schema: "agents",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    turn_id = table.Column<Guid>(type: "uuid", nullable: false),
                    gap_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_knowledge_gap_occurrences", x => new { x.tenant_id, x.turn_id });
                    table.ForeignKey(
                        name: "FK_knowledge_gap_occurrences_knowledge_gaps_tenant_id_gap_id",
                        columns: x => new { x.tenant_id, x.gap_id },
                        principalSchema: "agents",
                        principalTable: "knowledge_gaps",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_knowledge_gap_occurrences_tenant_id_gap_id_occurred_at",
                schema: "agents",
                table: "knowledge_gap_occurrences",
                columns: new[] { "tenant_id", "gap_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_knowledge_gaps_tenant_id_status_last_seen_at",
                schema: "agents",
                table: "knowledge_gaps",
                columns: new[] { "tenant_id", "status", "last_seen_at" });

            migrationBuilder.CreateIndex(
                name: "ux_knowledge_gaps_tenant_id_question_hash",
                schema: "agents",
                table: "knowledge_gaps",
                columns: new[] { "tenant_id", "question_hash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "knowledge_gap_occurrences",
                schema: "agents");

            migrationBuilder.DropTable(
                name: "knowledge_gaps",
                schema: "agents");
        }
    }
}
