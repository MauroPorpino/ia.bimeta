using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.Agents.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AgentsSemanticAnswer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "semantic_answer_settings",
                schema: "agents",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mode = table.Column<short>(type: "smallint", nullable: false),
                    min_score = table.Column<double>(type: "double precision", nullable: false),
                    min_margin = table.Column<double>(type: "double precision", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_semantic_answer_settings", x => x.tenant_id);
                    table.CheckConstraint("ck_semantic_answer_settings_min_margin", "min_margin >= 0 AND min_margin <= 0.5");
                    table.CheckConstraint("ck_semantic_answer_settings_min_score", "min_score >= 0.6 AND min_score <= 1");
                    table.CheckConstraint("ck_semantic_answer_settings_mode", "mode IN (0, 1, 2)");
                    table.CheckConstraint("ck_semantic_answer_settings_revision", "revision >= 1");
                });

            migrationBuilder.CreateTable(
                name: "semantic_decisions",
                schema: "agents",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    turn_id = table.Column<Guid>(type: "uuid", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    mode = table.Column<short>(type: "smallint", nullable: false),
                    outcome = table.Column<short>(type: "smallint", nullable: false),
                    entry_ordinal = table.Column<int>(type: "integer", nullable: true),
                    score = table.Column<double>(type: "double precision", nullable: true),
                    margin = table.Column<double>(type: "double precision", nullable: true),
                    definition_number = table.Column<int>(type: "integer", nullable: true),
                    embedding_ms = table.Column<int>(type: "integer", nullable: false),
                    search_ms = table.Column<int>(type: "integer", nullable: false),
                    failure_code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_semantic_decisions", x => new { x.tenant_id, x.turn_id });
                    table.CheckConstraint("ck_semantic_decisions_mode", "mode IN (1, 2)");
                    table.CheckConstraint("ck_semantic_decisions_outcome", "outcome IN (1, 2, 3, 4, 5, 6, 7, 8, 9)");
                    table.CheckConstraint("ck_semantic_decisions_times", "embedding_ms >= 0 AND search_ms >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "ix_semantic_decisions_tenant_id_decided_at",
                schema: "agents",
                table: "semantic_decisions",
                columns: new[] { "tenant_id", "decided_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "semantic_answer_settings",
                schema: "agents");

            migrationBuilder.DropTable(
                name: "semantic_decisions",
                schema: "agents");
        }
    }
}
