using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.ContactCenter.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FixPendingCheckNull : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_queue_items_pending",
                schema: "contact_center",
                table: "queue_items");

            // A restrição antiga deixava passar (NULL é aceito por um CHECK) uma linha sem tipo de operação e com o resto dela
            // preenchido. Para o aplicativo, linha sem tipo não tem operação pendente: limpar os campos órfãos não perde nada que
            // tenha significado e permite criar a restrição correta.
            migrationBuilder.Sql(
                "UPDATE contact_center.queue_items SET pending_operation_id = NULL, pending_transition_id = NULL, pending_transfer_id = NULL, " +
                "pending_queue_id = NULL, pending_user_id = NULL, pending_business_unit_id = NULL, pending_department_id = NULL, " +
                "pending_actor_user_id = NULL, pending_reason = NULL, pending_started_at = NULL " +
                "WHERE pending_kind IS NULL AND (pending_operation_id IS NOT NULL OR pending_transition_id IS NOT NULL OR pending_transfer_id IS NOT NULL " +
                "OR pending_queue_id IS NOT NULL OR pending_user_id IS NOT NULL OR pending_business_unit_id IS NOT NULL OR pending_department_id IS NOT NULL " +
                "OR pending_actor_user_id IS NOT NULL OR pending_reason IS NOT NULL OR pending_started_at IS NOT NULL);");

            migrationBuilder.AddCheckConstraint(
                name: "ck_queue_items_pending",
                schema: "contact_center",
                table: "queue_items",
                sql: "(pending_kind IS NULL AND pending_operation_id IS NULL AND pending_transition_id IS NULL AND pending_transfer_id IS NULL AND pending_queue_id IS NULL AND pending_user_id IS NULL AND pending_business_unit_id IS NULL AND pending_department_id IS NULL AND pending_actor_user_id IS NULL AND pending_reason IS NULL AND pending_started_at IS NULL) OR (pending_kind IS NOT NULL AND pending_kind IN (1, 2, 3, 4, 5) AND pending_operation_id IS NOT NULL AND pending_transition_id IS NOT NULL AND pending_transfer_id IS NOT NULL AND pending_queue_id IS NOT NULL AND pending_reason IS NOT NULL AND pending_started_at IS NOT NULL AND (pending_department_id IS NULL OR pending_business_unit_id IS NOT NULL))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_queue_items_pending",
                schema: "contact_center",
                table: "queue_items");

            migrationBuilder.AddCheckConstraint(
                name: "ck_queue_items_pending",
                schema: "contact_center",
                table: "queue_items",
                sql: "(pending_kind IS NULL AND pending_operation_id IS NULL AND pending_transition_id IS NULL AND pending_transfer_id IS NULL AND pending_queue_id IS NULL AND pending_user_id IS NULL AND pending_business_unit_id IS NULL AND pending_department_id IS NULL AND pending_actor_user_id IS NULL AND pending_reason IS NULL AND pending_started_at IS NULL) OR (pending_kind IN (1, 2, 3, 4, 5) AND pending_operation_id IS NOT NULL AND pending_transition_id IS NOT NULL AND pending_transfer_id IS NOT NULL AND pending_queue_id IS NOT NULL AND pending_reason IS NOT NULL AND pending_started_at IS NOT NULL AND (pending_department_id IS NULL OR pending_business_unit_id IS NOT NULL))");
        }
    }
}
