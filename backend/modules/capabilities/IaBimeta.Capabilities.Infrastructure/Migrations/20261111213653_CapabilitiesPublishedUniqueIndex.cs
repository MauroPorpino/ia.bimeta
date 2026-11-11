using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.Capabilities.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CapabilitiesPublishedUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_capability_definitions_draft",
                schema: "capabilities",
                table: "capability_definitions");

            migrationBuilder.CreateIndex(
                name: "ux_capability_definitions_draft",
                schema: "capabilities",
                table: "capability_definitions",
                columns: new[] { "tenant_id", "key", "status" },
                unique: true,
                filter: "status = 1");

            migrationBuilder.CreateIndex(
                name: "ux_capability_definitions_published",
                schema: "capabilities",
                table: "capability_definitions",
                columns: new[] { "tenant_id", "key" },
                unique: true,
                filter: "status = 2");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_capability_definitions_draft",
                schema: "capabilities",
                table: "capability_definitions");

            migrationBuilder.DropIndex(
                name: "ux_capability_definitions_published",
                schema: "capabilities",
                table: "capability_definitions");

            migrationBuilder.CreateIndex(
                name: "ux_capability_definitions_draft",
                schema: "capabilities",
                table: "capability_definitions",
                columns: new[] { "tenant_id", "key" },
                unique: true,
                filter: "status = 1");
        }
    }
}
