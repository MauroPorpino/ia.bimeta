using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.ContactCenter.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ProjectionConvergenceAndHistoryLimit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "history_limit_seq",
                schema: "contact_center",
                table: "queue_items",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "resync_state_version",
                schema: "contact_center",
                table: "queue_items",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_queue_items_tenant_id_resync",
                schema: "contact_center",
                table: "queue_items",
                columns: new[] { "tenant_id", "updated_at" },
                filter: "resync_state_version IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_queue_items_projection",
                schema: "contact_center",
                table: "queue_items",
                sql: "(resync_state_version IS NULL OR resync_state_version >= 0) AND (history_limit_seq IS NULL OR history_limit_seq >= 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_queue_items_tenant_id_resync",
                schema: "contact_center",
                table: "queue_items");

            migrationBuilder.DropCheckConstraint(
                name: "ck_queue_items_projection",
                schema: "contact_center",
                table: "queue_items");

            migrationBuilder.DropColumn(
                name: "history_limit_seq",
                schema: "contact_center",
                table: "queue_items");

            migrationBuilder.DropColumn(
                name: "resync_state_version",
                schema: "contact_center",
                table: "queue_items");
        }
    }
}
