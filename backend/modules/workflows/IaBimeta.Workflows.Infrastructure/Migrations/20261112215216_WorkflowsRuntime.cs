using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.Workflows.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class WorkflowsRuntime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "flow_activations",
                schema: "workflows",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_unit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    channel_instance_id = table.Column<Guid>(type: "uuid", nullable: true),
                    workflow_key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    rollout_percent = table.Column<int>(type: "integer", nullable: false),
                    on_failure = table.Column<short>(type: "smallint", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_flow_activations", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_flow_activations_on_failure", "on_failure IN (1, 2)");
                    table.CheckConstraint("ck_flow_activations_revision", "revision >= 1");
                    table.CheckConstraint("ck_flow_activations_rollout", "rollout_percent BETWEEN 0 AND 100");
                });

            migrationBuilder.CreateTable(
                name: "flow_effects",
                schema: "workflows",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    step = table.Column<long>(type: "bigint", nullable: false),
                    index = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    node_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    payload_cipher = table.Column<string>(type: "text", nullable: false),
                    payload_key_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    state = table.Column<short>(type: "smallint", nullable: false),
                    result_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_flow_effects", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_flow_effects_kind", "kind BETWEEN 1 AND 6");
                    table.CheckConstraint("ck_flow_effects_state", "state BETWEEN 1 AND 6");
                });

            migrationBuilder.CreateTable(
                name: "flow_instances",
                schema: "workflows",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workflow_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workflow_key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    activation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    on_failure = table.Column<short>(type: "smallint", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    node_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    step = table.Column<long>(type: "bigint", nullable: false),
                    wait_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    pending_effect_key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    failure_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    variables_cipher = table.Column<string>(type: "text", nullable: false),
                    variables_key_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    seen_message_seq = table.Column<long>(type: "bigint", nullable: false),
                    next_run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancel_reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    lease_owner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_token = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_flow_instances", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_flow_instances_on_failure", "on_failure IN (1, 2)");
                    table.CheckConstraint("ck_flow_instances_revision", "revision >= 1 AND step >= 0 AND attempts >= 0");
                    table.CheckConstraint("ck_flow_instances_status", "status BETWEEN 1 AND 9");
                });

            migrationBuilder.CreateTable(
                name: "flow_steps",
                schema: "workflows",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step = table.Column<long>(type: "bigint", nullable: false),
                    node_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    outcome = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_flow_steps", x => new { x.tenant_id, x.id });
                });

            migrationBuilder.CreateTable(
                name: "inbox_message",
                schema: "workflows",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    consumer = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inbox_message", x => new { x.tenant_id, x.consumer, x.event_id });
                });

            migrationBuilder.CreateTable(
                name: "routing_decisions",
                schema: "workflows",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner = table.Column<short>(type: "smallint", nullable: false),
                    activation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_routing_decisions", x => new { x.tenant_id, x.session_id });
                    table.CheckConstraint("ck_routing_decisions_owner", "owner IN (1, 2)");
                    table.CheckConstraint("ck_routing_decisions_revision", "revision >= 1");
                });

            migrationBuilder.CreateIndex(
                name: "ix_flow_activations_tenant_org_enabled",
                schema: "workflows",
                table: "flow_activations",
                columns: new[] { "tenant_id", "organization_id", "enabled" });

            migrationBuilder.CreateIndex(
                name: "ix_flow_effects_tenant_instance_state",
                schema: "workflows",
                table: "flow_effects",
                columns: new[] { "tenant_id", "instance_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ux_flow_effects_instance_key",
                schema: "workflows",
                table: "flow_effects",
                columns: new[] { "tenant_id", "instance_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_flow_instances_due",
                schema: "workflows",
                table: "flow_instances",
                column: "next_run_at",
                filter: "next_run_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_flow_instances_tenant_session_created",
                schema: "workflows",
                table: "flow_instances",
                columns: new[] { "tenant_id", "session_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_flow_instances_active_session",
                schema: "workflows",
                table: "flow_instances",
                columns: new[] { "tenant_id", "session_id" },
                unique: true,
                filter: "status < 5");

            migrationBuilder.CreateIndex(
                name: "ix_flow_steps_tenant_instance_step",
                schema: "workflows",
                table: "flow_steps",
                columns: new[] { "tenant_id", "instance_id", "step" });

            migrationBuilder.CreateIndex(
                name: "ix_flow_steps_tenant_node_at",
                schema: "workflows",
                table: "flow_steps",
                columns: new[] { "tenant_id", "node_id", "at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "flow_activations",
                schema: "workflows");

            migrationBuilder.DropTable(
                name: "flow_effects",
                schema: "workflows");

            migrationBuilder.DropTable(
                name: "flow_instances",
                schema: "workflows");

            migrationBuilder.DropTable(
                name: "flow_steps",
                schema: "workflows");

            migrationBuilder.DropTable(
                name: "inbox_message",
                schema: "workflows");

            migrationBuilder.DropTable(
                name: "routing_decisions",
                schema: "workflows");
        }
    }
}
