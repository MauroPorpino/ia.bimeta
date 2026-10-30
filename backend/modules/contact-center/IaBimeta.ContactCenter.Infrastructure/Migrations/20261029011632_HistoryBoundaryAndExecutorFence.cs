using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.ContactCenter.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class HistoryBoundaryAndExecutorFence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "executor_fence_seq",
                schema: "contact_center");

            migrationBuilder.AddColumn<long>(
                name: "closed_through_message_seq",
                schema: "contact_center",
                table: "queue_items",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "closed_through_message_seq",
                schema: "contact_center",
                table: "queue_items");

            migrationBuilder.DropSequence(
                name: "executor_fence_seq",
                schema: "contact_center");
        }
    }
}
