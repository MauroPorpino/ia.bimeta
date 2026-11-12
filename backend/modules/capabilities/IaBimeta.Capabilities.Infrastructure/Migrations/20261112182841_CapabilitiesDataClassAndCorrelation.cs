using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.Capabilities.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CapabilitiesDataClassAndCorrelation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "correlation_id",
                schema: "capabilities",
                table: "capability_executions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "data_class",
                schema: "capabilities",
                table: "capability_definitions",
                type: "smallint",
                nullable: false,
                defaultValue: (short)2);

            migrationBuilder.AddCheckConstraint(
                name: "ck_capability_definitions_data_class",
                schema: "capabilities",
                table: "capability_definitions",
                sql: "data_class IN (1, 2)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_capability_definitions_data_class",
                schema: "capabilities",
                table: "capability_definitions");

            migrationBuilder.DropColumn(
                name: "correlation_id",
                schema: "capabilities",
                table: "capability_executions");

            migrationBuilder.DropColumn(
                name: "data_class",
                schema: "capabilities",
                table: "capability_definitions");
        }
    }
}
