using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AuditOutboxRetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "attempts",
                schema: "identity",
                table: "audit_outbox",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "last_error",
                schema: "identity",
                table: "audit_outbox",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "next_attempt_at",
                schema: "identity",
                table: "audit_outbox",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "attempts",
                schema: "identity",
                table: "audit_outbox");

            migrationBuilder.DropColumn(
                name: "last_error",
                schema: "identity",
                table: "audit_outbox");

            migrationBuilder.DropColumn(
                name: "next_attempt_at",
                schema: "identity",
                table: "audit_outbox");
        }
    }
}
