using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.Conversations.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SessionPlacementEpoch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "placement_epoch",
                schema: "conversations",
                table: "service_sessions",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "placement_epoch",
                schema: "conversations",
                table: "service_sessions");
        }
    }
}
