using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.Conversations.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FlowMessageAuthor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_messages_direction_shape",
                schema: "conversations",
                table: "messages");

            migrationBuilder.AddCheckConstraint(
                name: "ck_messages_direction_shape",
                schema: "conversations",
                table: "messages",
                sql: "(direction = 1 AND inbound_seq IS NOT NULL AND provider_event_id IS NOT NULL AND author = 1 AND delivery_state IS NULL) OR (direction = 2 AND inbound_seq IS NULL AND delivery_state IS NOT NULL AND author IN (2, 3, 4))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_messages_direction_shape",
                schema: "conversations",
                table: "messages");

            migrationBuilder.AddCheckConstraint(
                name: "ck_messages_direction_shape",
                schema: "conversations",
                table: "messages",
                sql: "(direction = 1 AND inbound_seq IS NOT NULL AND provider_event_id IS NOT NULL AND author = 1 AND delivery_state IS NULL) OR (direction = 2 AND inbound_seq IS NULL AND delivery_state IS NOT NULL AND author IN (2, 3))");
        }
    }
}
