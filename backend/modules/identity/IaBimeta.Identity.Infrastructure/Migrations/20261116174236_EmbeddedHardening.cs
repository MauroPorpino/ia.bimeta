using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EmbeddedHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "issuer_key_id",
                schema: "identity",
                table: "sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "key_id",
                schema: "identity",
                table: "launch_tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "session_id",
                schema: "identity",
                table: "launch_tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "sessions_revoked_at",
                schema: "identity",
                table: "integration_connections",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "sessions_revoked_at",
                schema: "identity",
                table: "external_identities",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "issuer_key_id",
                schema: "identity",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "key_id",
                schema: "identity",
                table: "launch_tickets");

            migrationBuilder.DropColumn(
                name: "session_id",
                schema: "identity",
                table: "launch_tickets");

            migrationBuilder.DropColumn(
                name: "sessions_revoked_at",
                schema: "identity",
                table: "integration_connections");

            migrationBuilder.DropColumn(
                name: "sessions_revoked_at",
                schema: "identity",
                table: "external_identities");
        }
    }
}
