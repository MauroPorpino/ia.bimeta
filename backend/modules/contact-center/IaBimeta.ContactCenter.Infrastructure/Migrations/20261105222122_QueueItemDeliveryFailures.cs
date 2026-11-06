using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.ContactCenter.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class QueueItemDeliveryFailures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "delivery_failures",
                schema: "contact_center",
                table: "queue_items",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_delivery_failure_at",
                schema: "contact_center",
                table: "queue_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_queue_items_delivery_failures",
                schema: "contact_center",
                table: "queue_items",
                sql: "delivery_failures >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_queue_items_delivery_failures",
                schema: "contact_center",
                table: "queue_items");

            migrationBuilder.DropColumn(
                name: "delivery_failures",
                schema: "contact_center",
                table: "queue_items");

            migrationBuilder.DropColumn(
                name: "last_delivery_failure_at",
                schema: "contact_center",
                table: "queue_items");
        }
    }
}
