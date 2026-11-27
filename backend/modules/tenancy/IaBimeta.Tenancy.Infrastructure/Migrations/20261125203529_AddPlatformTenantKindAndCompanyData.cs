using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.Tenancy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformTenantKindAndCompanyData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "contact_email",
                schema: "tenancy",
                table: "tenants",
                type: "character varying(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "contact_name",
                schema: "tenancy",
                table: "tenants",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "document",
                schema: "tenancy",
                table: "tenants",
                type: "character varying(14)",
                maxLength: 14,
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "kind",
                schema: "tenancy",
                table: "tenants",
                type: "smallint",
                nullable: false,
                defaultValue: (short)1);

            migrationBuilder.AddColumn<string>(
                name: "legal_name",
                schema: "tenancy",
                table: "tenants",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "reactivated_at",
                schema: "tenancy",
                table: "tenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "suspended_at",
                schema: "tenancy",
                table: "tenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                schema: "tenancy",
                table: "tenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "tenant_status_changes",
                schema: "tenancy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_status = table.Column<short>(type: "smallint", nullable: false),
                    to_status = table.Column<short>(type: "smallint", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_status_changes", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_tenants_platform",
                schema: "tenancy",
                table: "tenants",
                column: "kind",
                unique: true,
                filter: "kind = 2");

            migrationBuilder.CreateIndex(
                name: "ix_tenant_status_changes_tenant",
                schema: "tenancy",
                table: "tenant_status_changes",
                columns: new[] { "tenant_id", "changed_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenant_status_changes",
                schema: "tenancy");

            migrationBuilder.DropIndex(
                name: "ux_tenants_platform",
                schema: "tenancy",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "contact_email",
                schema: "tenancy",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "contact_name",
                schema: "tenancy",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "document",
                schema: "tenancy",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "kind",
                schema: "tenancy",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "legal_name",
                schema: "tenancy",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "reactivated_at",
                schema: "tenancy",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "suspended_at",
                schema: "tenancy",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "updated_at",
                schema: "tenancy",
                table: "tenants");
        }
    }
}
