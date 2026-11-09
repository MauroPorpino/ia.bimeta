using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.Conversations.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class OutboundAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "provider_media_id",
                schema: "conversations",
                table: "message_attachments",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256);

            migrationBuilder.AddColumn<short>(
                name: "direction",
                schema: "conversations",
                table: "message_attachments",
                type: "smallint",
                nullable: false,
                defaultValue: (short)1); // os arquivos já gravados são todos do cliente (entrada); o do atendente (2) só nasce com este código

            migrationBuilder.AddCheckConstraint(
                name: "ck_message_attachments_direction",
                schema: "conversations",
                table: "message_attachments",
                sql: "direction IN (1, 2)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_message_attachments_outbound_stored",
                schema: "conversations",
                table: "message_attachments",
                sql: "direction = 1 OR status <> 1");

            migrationBuilder.AddCheckConstraint(
                name: "ck_message_attachments_provider_media",
                schema: "conversations",
                table: "message_attachments",
                sql: "(direction = 1 AND provider_media_id IS NOT NULL) OR (direction = 2 AND provider_media_id IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_message_attachments_direction",
                schema: "conversations",
                table: "message_attachments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_message_attachments_outbound_stored",
                schema: "conversations",
                table: "message_attachments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_message_attachments_provider_media",
                schema: "conversations",
                table: "message_attachments");

            migrationBuilder.DropColumn(
                name: "direction",
                schema: "conversations",
                table: "message_attachments");

            migrationBuilder.AlterColumn<string>(
                name: "provider_media_id",
                schema: "conversations",
                table: "message_attachments",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256,
                oldNullable: true);
        }
    }
}
