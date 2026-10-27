using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.Tenancy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBusinessStructure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "business_units",
                schema: "tenancy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_business_units", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "FK_business_units_organizations_tenant_id_organization_id",
                        columns: x => new { x.tenant_id, x.organization_id },
                        principalSchema: "tenancy",
                        principalTable: "organizations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "departments",
                schema: "tenancy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_departments", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "FK_departments_business_units_tenant_id_business_unit_id",
                        columns: x => new { x.tenant_id, x.business_unit_id },
                        principalSchema: "tenancy",
                        principalTable: "business_units",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_business_units_one_headquarters",
                schema: "tenancy",
                table: "business_units",
                columns: new[] { "tenant_id", "organization_id" },
                unique: true,
                filter: "kind = 1");

            migrationBuilder.CreateIndex(
                name: "ix_business_units_tenant_id_organization_id_name",
                schema: "tenancy",
                table: "business_units",
                columns: new[] { "tenant_id", "organization_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_departments_tenant_id_business_unit_id_name",
                schema: "tenancy",
                table: "departments",
                columns: new[] { "tenant_id", "business_unit_id", "name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "departments",
                schema: "tenancy");

            migrationBuilder.DropTable(
                name: "business_units",
                schema: "tenancy");
        }
    }
}
