using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.ContactCenter.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class QueueItemsAndHandoff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "queue_items",
                schema: "contact_center",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_unit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    queue_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    assigned_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    entered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    assigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    close_reason = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    pending_kind = table.Column<short>(type: "smallint", nullable: true),
                    pending_operation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    pending_transition_id = table.Column<Guid>(type: "uuid", nullable: true),
                    pending_transfer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    pending_queue_id = table.Column<Guid>(type: "uuid", nullable: true),
                    pending_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    pending_business_unit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    pending_department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    pending_actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    pending_reason = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    pending_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    pending_attempts = table.Column<int>(type: "integer", nullable: false),
                    pending_last_error = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_queue_items", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_queue_items_assigned", "(status = 2) = (assigned_user_id IS NOT NULL)");
                    table.CheckConstraint("ck_queue_items_closed", "(status = 3) = (closed_at IS NOT NULL) AND (status <> 3 OR pending_kind IS NULL)");
                    table.CheckConstraint("ck_queue_items_department", "department_id IS NULL OR business_unit_id IS NOT NULL");
                    table.CheckConstraint("ck_queue_items_pending", "(pending_kind IS NULL AND pending_operation_id IS NULL AND pending_transition_id IS NULL AND pending_transfer_id IS NULL AND pending_queue_id IS NULL AND pending_user_id IS NULL AND pending_business_unit_id IS NULL AND pending_department_id IS NULL AND pending_actor_user_id IS NULL AND pending_reason IS NULL AND pending_started_at IS NULL) OR (pending_kind IN (1, 2, 3, 4, 5) AND pending_operation_id IS NOT NULL AND pending_transition_id IS NOT NULL AND pending_transfer_id IS NOT NULL AND pending_queue_id IS NOT NULL AND pending_reason IS NOT NULL AND pending_started_at IS NOT NULL AND (pending_department_id IS NULL OR pending_business_unit_id IS NOT NULL))");
                    table.CheckConstraint("ck_queue_items_status", "status IN (1, 2, 3)");
                    table.CheckConstraint("ck_queue_items_version", "version >= 1");
                    table.ForeignKey(
                        name: "FK_queue_items_service_queues_tenant_id_queue_id",
                        columns: x => new { x.tenant_id, x.queue_id },
                        principalSchema: "contact_center",
                        principalTable: "service_queues",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "queue_item_history",
                schema: "contact_center",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    from_queue_id = table.Column<Guid>(type: "uuid", nullable: true),
                    to_queue_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    to_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    state_version_after = table.Column<long>(type: "bigint", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_queue_item_history", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_queue_item_history_kind", "kind IN (1, 2, 3, 4, 5, 6, 7)");
                    table.ForeignKey(
                        name: "FK_queue_item_history_queue_items_tenant_id_item_id",
                        columns: x => new { x.tenant_id, x.item_id },
                        principalSchema: "contact_center",
                        principalTable: "queue_items",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_queue_item_history_tenant_id_item_id_occurred_at_id",
                schema: "contact_center",
                table: "queue_item_history",
                columns: new[] { "tenant_id", "item_id", "occurred_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ux_queue_item_history_operation",
                schema: "contact_center",
                table: "queue_item_history",
                columns: new[] { "tenant_id", "operation_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_queue_items_tenant_id_assigned_user_id",
                schema: "contact_center",
                table: "queue_items",
                columns: new[] { "tenant_id", "assigned_user_id" },
                filter: "assigned_user_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_queue_items_tenant_id_pending_started_at",
                schema: "contact_center",
                table: "queue_items",
                columns: new[] { "tenant_id", "pending_started_at" },
                filter: "pending_kind IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_queue_items_tenant_id_queue_id_status_entered_at",
                schema: "contact_center",
                table: "queue_items",
                columns: new[] { "tenant_id", "queue_id", "status", "entered_at" });

            migrationBuilder.CreateIndex(
                name: "ux_queue_items_active_session",
                schema: "contact_center",
                table: "queue_items",
                columns: new[] { "tenant_id", "session_id" },
                unique: true,
                filter: "status <> 3");

            // O histórico de atribuições e transferências é só de acréscimo, garantido pelo banco: alterar, excluir ou truncar um
            // registro é recusado, mesmo por quem contornar o aplicativo.
            migrationBuilder.Sql(@"
CREATE FUNCTION contact_center.reject_history_change() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'O historico da fila so aceita inclusoes: % em contact_center.queue_item_history nao e permitido.', TG_OP
        USING ERRCODE = 'restrict_violation';
END;
$$;");

            migrationBuilder.Sql(
                "CREATE TRIGGER trg_queue_item_history_append_only BEFORE UPDATE OR DELETE ON contact_center.queue_item_history " +
                "FOR EACH ROW EXECUTE FUNCTION contact_center.reject_history_change();");

            migrationBuilder.Sql(
                "CREATE TRIGGER trg_queue_item_history_no_truncate BEFORE TRUNCATE ON contact_center.queue_item_history " +
                "FOR EACH STATEMENT EXECUTE FUNCTION contact_center.reject_history_change();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "queue_item_history",
                schema: "contact_center");

            migrationBuilder.DropTable(
                name: "queue_items",
                schema: "contact_center");

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS contact_center.reject_history_change();");
        }
    }
}
