using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddInitialAdministratorFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "initial_administrator",
                schema: "identity",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "initial_administrator",
                schema: "identity",
                table: "users");
        }
    }
}
