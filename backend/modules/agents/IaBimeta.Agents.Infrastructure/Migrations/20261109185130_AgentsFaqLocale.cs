using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.Agents.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AgentsFaqLocale : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "faq_locale",
                schema: "agents",
                table: "agent_definition_versions",
                type: "character varying(35)",
                maxLength: 35,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "faq_locale",
                schema: "agents",
                table: "agent_definition_versions");
        }
    }
}
