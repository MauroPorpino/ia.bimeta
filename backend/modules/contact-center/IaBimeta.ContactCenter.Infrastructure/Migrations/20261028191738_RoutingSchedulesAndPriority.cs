using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.ContactCenter.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RoutingSchedulesAndPriority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_queue_items_tenant_id_queue_id_status_entered_at",
                schema: "contact_center",
                table: "queue_items");

            migrationBuilder.DropCheckConstraint(
                name: "ck_queue_item_history_kind",
                schema: "contact_center",
                table: "queue_item_history");

            migrationBuilder.AddColumn<int>(
                name: "default_priority",
                schema: "contact_center",
                table: "service_queues",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "fallback_queue_id",
                schema: "contact_center",
                table: "service_queues",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "max_items_per_agent",
                schema: "contact_center",
                table: "service_queues",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "no_agent",
                schema: "contact_center",
                table: "service_queues",
                type: "smallint",
                nullable: false,
                defaultValue: (short)1);

            migrationBuilder.AddColumn<short>(
                name: "out_of_hours",
                schema: "contact_center",
                table: "service_queues",
                type: "smallint",
                nullable: false,
                defaultValue: (short)1);

            migrationBuilder.AddColumn<string>(
                name: "time_zone",
                schema: "contact_center",
                table: "service_queues",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "priority",
                schema: "contact_center",
                table: "queue_items",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "routing_rule_id",
                schema: "contact_center",
                table: "queue_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "from_priority",
                schema: "contact_center",
                table: "queue_item_history",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "to_priority",
                schema: "contact_center",
                table: "queue_item_history",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "agent_presences",
                schema: "contact_center",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_presences", x => new { x.tenant_id, x.user_id });
                    table.CheckConstraint("ck_agent_presences_status", "status IN (1, 2, 3)");
                    table.CheckConstraint("ck_agent_presences_version", "version >= 1");
                });

            migrationBuilder.CreateTable(
                name: "queue_closures",
                schema: "contact_center",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    queue_id = table.Column<Guid>(type: "uuid", nullable: false),
                    closed_on = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_queue_closures", x => new { x.tenant_id, x.queue_id, x.closed_on });
                    table.ForeignKey(
                        name: "FK_queue_closures_service_queues_tenant_id_queue_id",
                        columns: x => new { x.tenant_id, x.queue_id },
                        principalSchema: "contact_center",
                        principalTable: "service_queues",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "queue_schedule_windows",
                schema: "contact_center",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    queue_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day_of_week = table.Column<short>(type: "smallint", nullable: false),
                    start_minute = table.Column<int>(type: "integer", nullable: false),
                    end_minute = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_queue_schedule_windows", x => new { x.tenant_id, x.queue_id, x.day_of_week, x.start_minute });
                    table.CheckConstraint("ck_queue_schedule_windows_day", "day_of_week BETWEEN 0 AND 6");
                    table.CheckConstraint("ck_queue_schedule_windows_minutes", "start_minute >= 0 AND end_minute <= 1440 AND end_minute > start_minute");
                    table.ForeignKey(
                        name: "FK_queue_schedule_windows_service_queues_tenant_id_queue_id",
                        columns: x => new { x.tenant_id, x.queue_id },
                        principalSchema: "contact_center",
                        principalTable: "service_queues",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "routing_rules",
                schema: "contact_center",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    channel_instance_id = table.Column<Guid>(type: "uuid", nullable: true),
                    business_unit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    queue_id = table.Column<Guid>(type: "uuid", nullable: false),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_routing_rules", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_routing_rules_department", "department_id IS NULL OR business_unit_id IS NOT NULL");
                    table.CheckConstraint("ck_routing_rules_priority", "priority BETWEEN 0 AND 9");
                    table.CheckConstraint("ck_routing_rules_version", "version >= 1");
                    table.ForeignKey(
                        name: "FK_routing_rules_service_queues_tenant_id_queue_id",
                        columns: x => new { x.tenant_id, x.queue_id },
                        principalSchema: "contact_center",
                        principalTable: "service_queues",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_service_queues_tenant_id_fallback_queue_id",
                schema: "contact_center",
                table: "service_queues",
                columns: new[] { "tenant_id", "fallback_queue_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_service_queues_default_priority",
                schema: "contact_center",
                table: "service_queues",
                sql: "default_priority BETWEEN 0 AND 9");

            migrationBuilder.AddCheckConstraint(
                name: "ck_service_queues_fallback",
                schema: "contact_center",
                table: "service_queues",
                sql: "(out_of_hours <> 3 AND no_agent <> 2) OR fallback_queue_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_service_queues_fallback_self",
                schema: "contact_center",
                table: "service_queues",
                sql: "fallback_queue_id IS NULL OR fallback_queue_id <> id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_service_queues_max_items",
                schema: "contact_center",
                table: "service_queues",
                sql: "max_items_per_agent IS NULL OR max_items_per_agent >= 1");

            migrationBuilder.AddCheckConstraint(
                name: "ck_service_queues_no_agent",
                schema: "contact_center",
                table: "service_queues",
                sql: "no_agent IN (1, 2)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_service_queues_out_of_hours",
                schema: "contact_center",
                table: "service_queues",
                sql: "out_of_hours IN (1, 2, 3)");

            migrationBuilder.CreateIndex(
                name: "ix_queue_items_tenant_id_queue_id_status_priority_entered_at",
                schema: "contact_center",
                table: "queue_items",
                columns: new[] { "tenant_id", "queue_id", "status", "priority", "entered_at" },
                descending: new[] { false, false, false, true, false });

            migrationBuilder.AddCheckConstraint(
                name: "ck_queue_items_priority",
                schema: "contact_center",
                table: "queue_items",
                sql: "priority BETWEEN 0 AND 9");

            migrationBuilder.AddCheckConstraint(
                name: "ck_queue_item_history_kind",
                schema: "contact_center",
                table: "queue_item_history",
                sql: "kind IN (1, 2, 3, 4, 5, 6, 7, 8)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_queue_item_history_priority",
                schema: "contact_center",
                table: "queue_item_history",
                sql: "(from_priority IS NULL OR from_priority BETWEEN 0 AND 9) AND (to_priority IS NULL OR to_priority BETWEEN 0 AND 9)");

            migrationBuilder.CreateIndex(
                name: "ix_routing_rules_tenant_id_organization_id_enabled_sort_order",
                schema: "contact_center",
                table: "routing_rules",
                columns: new[] { "tenant_id", "organization_id", "enabled", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "ix_routing_rules_tenant_id_queue_id",
                schema: "contact_center",
                table: "routing_rules",
                columns: new[] { "tenant_id", "queue_id" });

            migrationBuilder.AddForeignKey(
                name: "FK_service_queues_service_queues_tenant_id_fallback_queue_id",
                schema: "contact_center",
                table: "service_queues",
                columns: new[] { "tenant_id", "fallback_queue_id" },
                principalSchema: "contact_center",
                principalTable: "service_queues",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_service_queues_service_queues_tenant_id_fallback_queue_id",
                schema: "contact_center",
                table: "service_queues");

            migrationBuilder.DropTable(
                name: "agent_presences",
                schema: "contact_center");

            migrationBuilder.DropTable(
                name: "queue_closures",
                schema: "contact_center");

            migrationBuilder.DropTable(
                name: "queue_schedule_windows",
                schema: "contact_center");

            migrationBuilder.DropTable(
                name: "routing_rules",
                schema: "contact_center");

            migrationBuilder.DropIndex(
                name: "IX_service_queues_tenant_id_fallback_queue_id",
                schema: "contact_center",
                table: "service_queues");

            migrationBuilder.DropCheckConstraint(
                name: "ck_service_queues_default_priority",
                schema: "contact_center",
                table: "service_queues");

            migrationBuilder.DropCheckConstraint(
                name: "ck_service_queues_fallback",
                schema: "contact_center",
                table: "service_queues");

            migrationBuilder.DropCheckConstraint(
                name: "ck_service_queues_fallback_self",
                schema: "contact_center",
                table: "service_queues");

            migrationBuilder.DropCheckConstraint(
                name: "ck_service_queues_max_items",
                schema: "contact_center",
                table: "service_queues");

            migrationBuilder.DropCheckConstraint(
                name: "ck_service_queues_no_agent",
                schema: "contact_center",
                table: "service_queues");

            migrationBuilder.DropCheckConstraint(
                name: "ck_service_queues_out_of_hours",
                schema: "contact_center",
                table: "service_queues");

            migrationBuilder.DropIndex(
                name: "ix_queue_items_tenant_id_queue_id_status_priority_entered_at",
                schema: "contact_center",
                table: "queue_items");

            migrationBuilder.DropCheckConstraint(
                name: "ck_queue_items_priority",
                schema: "contact_center",
                table: "queue_items");

            migrationBuilder.DropCheckConstraint(
                name: "ck_queue_item_history_kind",
                schema: "contact_center",
                table: "queue_item_history");

            migrationBuilder.DropCheckConstraint(
                name: "ck_queue_item_history_priority",
                schema: "contact_center",
                table: "queue_item_history");

            migrationBuilder.DropColumn(
                name: "default_priority",
                schema: "contact_center",
                table: "service_queues");

            migrationBuilder.DropColumn(
                name: "fallback_queue_id",
                schema: "contact_center",
                table: "service_queues");

            migrationBuilder.DropColumn(
                name: "max_items_per_agent",
                schema: "contact_center",
                table: "service_queues");

            migrationBuilder.DropColumn(
                name: "no_agent",
                schema: "contact_center",
                table: "service_queues");

            migrationBuilder.DropColumn(
                name: "out_of_hours",
                schema: "contact_center",
                table: "service_queues");

            migrationBuilder.DropColumn(
                name: "time_zone",
                schema: "contact_center",
                table: "service_queues");

            migrationBuilder.DropColumn(
                name: "priority",
                schema: "contact_center",
                table: "queue_items");

            migrationBuilder.DropColumn(
                name: "routing_rule_id",
                schema: "contact_center",
                table: "queue_items");

            migrationBuilder.DropColumn(
                name: "from_priority",
                schema: "contact_center",
                table: "queue_item_history");

            migrationBuilder.DropColumn(
                name: "to_priority",
                schema: "contact_center",
                table: "queue_item_history");

            migrationBuilder.CreateIndex(
                name: "ix_queue_items_tenant_id_queue_id_status_entered_at",
                schema: "contact_center",
                table: "queue_items",
                columns: new[] { "tenant_id", "queue_id", "status", "entered_at" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_queue_item_history_kind",
                schema: "contact_center",
                table: "queue_item_history",
                sql: "kind IN (1, 2, 3, 4, 5, 6, 7)");
        }
    }
}
