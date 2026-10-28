using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserPreferredLocale : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "preferred_locale",
                schema: "identity",
                table: "users",
                type: "character varying(35)",
                maxLength: 35,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_preferred_locale",
                schema: "identity",
                table: "users",
                sql: "preferred_locale IS NULL OR preferred_locale ~ '^[A-Za-z]{2,8}(-[A-Za-z0-9]{1,8})*$'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_users_preferred_locale",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "preferred_locale",
                schema: "identity",
                table: "users");
        }
    }
}
