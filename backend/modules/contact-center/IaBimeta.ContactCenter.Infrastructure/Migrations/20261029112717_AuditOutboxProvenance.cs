using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.ContactCenter.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AuditOutboxProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "actor_origin",
                schema: "contact_center",
                table: "audit_outbox",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "recovered");

            migrationBuilder.AddColumn<Guid>(
                name: "actor_user_id",
                schema: "contact_center",
                table: "audit_outbox",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "actor_origin",
                schema: "contact_center",
                table: "audit_outbox");

            migrationBuilder.DropColumn(
                name: "actor_user_id",
                schema: "contact_center",
                table: "audit_outbox");
        }
    }
}
