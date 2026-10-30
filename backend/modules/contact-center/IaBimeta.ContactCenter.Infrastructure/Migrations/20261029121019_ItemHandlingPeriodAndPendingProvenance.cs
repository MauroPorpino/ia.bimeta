using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.ContactCenter.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ItemHandlingPeriodAndPendingProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_queue_items_pending",
                schema: "contact_center",
                table: "queue_items");

            migrationBuilder.AddColumn<long>(
                name: "handling_period",
                schema: "contact_center",
                table: "queue_items",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pending_actor_origin",
                schema: "contact_center",
                table: "queue_items",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_queue_items_pending",
                schema: "contact_center",
                table: "queue_items",
                sql: "(pending_kind IS NULL AND pending_operation_id IS NULL AND pending_transition_id IS NULL AND pending_transfer_id IS NULL AND pending_queue_id IS NULL AND pending_user_id IS NULL AND pending_business_unit_id IS NULL AND pending_department_id IS NULL AND pending_actor_user_id IS NULL AND pending_actor_origin IS NULL AND pending_reason IS NULL AND pending_started_at IS NULL AND pending_next_attempt_at IS NULL) OR (pending_kind IS NOT NULL AND pending_kind IN (1, 2, 3, 4, 5) AND pending_operation_id IS NOT NULL AND pending_transition_id IS NOT NULL AND pending_transfer_id IS NOT NULL AND pending_queue_id IS NOT NULL AND pending_reason IS NOT NULL AND pending_started_at IS NOT NULL AND (pending_department_id IS NULL OR pending_business_unit_id IS NOT NULL))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_queue_items_pending",
                schema: "contact_center",
                table: "queue_items");

            migrationBuilder.DropColumn(
                name: "handling_period",
                schema: "contact_center",
                table: "queue_items");

            migrationBuilder.DropColumn(
                name: "pending_actor_origin",
                schema: "contact_center",
                table: "queue_items");

            migrationBuilder.AddCheckConstraint(
                name: "ck_queue_items_pending",
                schema: "contact_center",
                table: "queue_items",
                sql: "(pending_kind IS NULL AND pending_operation_id IS NULL AND pending_transition_id IS NULL AND pending_transfer_id IS NULL AND pending_queue_id IS NULL AND pending_user_id IS NULL AND pending_business_unit_id IS NULL AND pending_department_id IS NULL AND pending_actor_user_id IS NULL AND pending_reason IS NULL AND pending_started_at IS NULL AND pending_next_attempt_at IS NULL) OR (pending_kind IS NOT NULL AND pending_kind IN (1, 2, 3, 4, 5) AND pending_operation_id IS NOT NULL AND pending_transition_id IS NOT NULL AND pending_transfer_id IS NOT NULL AND pending_queue_id IS NOT NULL AND pending_reason IS NOT NULL AND pending_started_at IS NOT NULL AND (pending_department_id IS NULL OR pending_business_unit_id IS NOT NULL))");
        }
    }
}
