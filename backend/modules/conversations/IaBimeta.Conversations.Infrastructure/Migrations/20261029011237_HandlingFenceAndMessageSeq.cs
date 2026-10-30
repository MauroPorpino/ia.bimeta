using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.Conversations.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class HandlingFenceAndMessageSeq : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "handling_fence_token",
                schema: "conversations",
                table: "service_sessions",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "message_seq_at",
                schema: "conversations",
                table: "handling_transitions",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "handling_fence_token",
                schema: "conversations",
                table: "service_sessions");

            migrationBuilder.DropColumn(
                name: "message_seq_at",
                schema: "conversations",
                table: "handling_transitions");
        }
    }
}
