using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.ContactCenter.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class OperationReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "queue_operations",
                schema: "contact_center",
                columns: table => new
                {
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    queue_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    state = table.Column<short>(type: "smallint", nullable: false),
                    failure_rule = table.Column<int>(type: "integer", nullable: true),
                    snapshot = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_queue_operations", x => new { x.tenant_id, x.operation_id });
                    table.CheckConstraint("ck_queue_operations_completion", "(state = 1 AND completed_at IS NULL AND snapshot IS NULL AND failure_rule IS NULL) OR (state = 4 AND completed_at IS NOT NULL AND snapshot IS NULL AND failure_rule IS NOT NULL) OR (state IN (2, 3, 5) AND completed_at IS NOT NULL AND snapshot IS NOT NULL AND failure_rule IS NULL)");
                    table.CheckConstraint("ck_queue_operations_kind", "kind IN (1, 2, 3, 4, 5, 6, 7)");
                    table.CheckConstraint("ck_queue_operations_state", "state IN (1, 2, 3, 4, 5)");
                    table.ForeignKey(
                        name: "FK_queue_operations_queue_items_tenant_id_item_id",
                        columns: x => new { x.tenant_id, x.item_id },
                        principalSchema: "contact_center",
                        principalTable: "queue_items",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_queue_operations_tenant_id_item_id",
                schema: "contact_center",
                table: "queue_operations",
                columns: new[] { "tenant_id", "item_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "queue_operations",
                schema: "contact_center");
        }
    }
}
