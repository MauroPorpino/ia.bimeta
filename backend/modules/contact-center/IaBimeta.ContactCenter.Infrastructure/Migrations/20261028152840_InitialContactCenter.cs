using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.ContactCenter.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialContactCenter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "contact_center");

            migrationBuilder.CreateTable(
                name: "service_queues",
                schema: "contact_center",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_unit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    name_key = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_queues", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_service_queues_department", "department_id IS NULL OR business_unit_id IS NOT NULL");
                    table.CheckConstraint("ck_service_queues_status", "status IN (1, 2)");
                    table.CheckConstraint("ck_service_queues_version", "version >= 1");
                });

            migrationBuilder.CreateTable(
                name: "queue_memberships",
                schema: "contact_center",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    queue_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<short>(type: "smallint", nullable: false),
                    added_by = table.Column<Guid>(type: "uuid", nullable: true),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_queue_memberships", x => new { x.tenant_id, x.queue_id, x.user_id });
                    table.CheckConstraint("ck_queue_memberships_role", "role IN (1, 2)");
                    table.ForeignKey(
                        name: "FK_queue_memberships_service_queues_tenant_id_queue_id",
                        columns: x => new { x.tenant_id, x.queue_id },
                        principalSchema: "contact_center",
                        principalTable: "service_queues",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_queue_memberships_tenant_id_user_id",
                schema: "contact_center",
                table: "queue_memberships",
                columns: new[] { "tenant_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_service_queues_tenant_id_organization_id_status",
                schema: "contact_center",
                table: "service_queues",
                columns: new[] { "tenant_id", "organization_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_service_queues_organization_name",
                schema: "contact_center",
                table: "service_queues",
                columns: new[] { "tenant_id", "organization_id", "name_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "queue_memberships",
                schema: "contact_center");

            migrationBuilder.DropTable(
                name: "service_queues",
                schema: "contact_center");
        }
    }
}
