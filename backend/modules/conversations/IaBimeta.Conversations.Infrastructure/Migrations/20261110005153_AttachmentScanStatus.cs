using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.Conversations.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AttachmentScanStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<short>(
                name: "scan_status",
                schema: "conversations",
                table: "message_attachments",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "scanned_at",
                schema: "conversations",
                table: "message_attachments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_message_attachments_scan_status",
                schema: "conversations",
                table: "message_attachments",
                sql: "scan_status IN (0, 1)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_message_attachments_scan_status",
                schema: "conversations",
                table: "message_attachments");

            migrationBuilder.DropColumn(
                name: "scan_status",
                schema: "conversations",
                table: "message_attachments");

            migrationBuilder.DropColumn(
                name: "scanned_at",
                schema: "conversations",
                table: "message_attachments");
        }
    }
}
