using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.Agents.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AgentsFaqSuggestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "faq_suggestions",
                schema: "agents",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    gap_id = table.Column<Guid>(type: "uuid", nullable: true),
                    dedupe_key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    payload_json = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    model_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    applied_draft_version = table.Column<int>(type: "integer", nullable: true),
                    edits = table.Column<int>(type: "integer", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_faq_suggestions", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_faq_suggestions_applied", "applied_draft_version IS NULL OR (status = 2 AND applied_draft_version >= 1)");
                    table.CheckConstraint("ck_faq_suggestions_decided", "(status = 1) = (decided_at IS NULL)");
                    table.CheckConstraint("ck_faq_suggestions_kind", "kind IN (1, 2, 3, 4)");
                    table.CheckConstraint("ck_faq_suggestions_payload", "octet_length(payload_json) <= 8000");
                    table.CheckConstraint("ck_faq_suggestions_revision", "revision >= 1 AND edits >= 0");
                    table.CheckConstraint("ck_faq_suggestions_status", "status IN (1, 2, 3)");
                });

            migrationBuilder.CreateIndex(
                name: "ix_faq_suggestions_tenant_id_status_created_at",
                schema: "agents",
                table: "faq_suggestions",
                columns: new[] { "tenant_id", "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_faq_suggestions_pending_key",
                schema: "agents",
                table: "faq_suggestions",
                columns: new[] { "tenant_id", "dedupe_key" },
                unique: true,
                filter: "status = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "faq_suggestions",
                schema: "agents");
        }
    }
}
