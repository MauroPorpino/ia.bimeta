using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.Conversations.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MessageAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "message_attachments",
                schema: "conversations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    provider_media_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    declared_mime_type = table.Column<string>(type: "character varying(127)", maxLength: 127, nullable: true),
                    mime_type = table.Column<string>(type: "character varying(127)", maxLength: 127, nullable: true),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    caption = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    storage_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    failure_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_message_attachments", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_message_attachments_available", "(status = 2 AND storage_key IS NOT NULL AND mime_type IS NOT NULL AND size_bytes IS NOT NULL AND size_bytes >= 1 AND sha256 IS NOT NULL AND failure_code IS NULL) OR (status <> 2 AND storage_key IS NULL AND mime_type IS NULL AND size_bytes IS NULL AND sha256 IS NULL)");
                    table.CheckConstraint("ck_message_attachments_failure", "(status IN (3, 4) AND failure_code IS NOT NULL) OR (status IN (1, 2) AND failure_code IS NULL)");
                    table.CheckConstraint("ck_message_attachments_kind", "kind IN (2, 3, 4)");
                    table.CheckConstraint("ck_message_attachments_status", "status IN (1, 2, 3, 4)");
                    table.ForeignKey(
                        name: "FK_message_attachments_messages_tenant_id_message_id",
                        columns: x => new { x.tenant_id, x.message_id },
                        principalSchema: "conversations",
                        principalTable: "messages",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_message_attachments_tenant_id_session_id",
                schema: "conversations",
                table: "message_attachments",
                columns: new[] { "tenant_id", "session_id" });

            migrationBuilder.CreateIndex(
                name: "ux_message_attachments_tenant_id_message_id",
                schema: "conversations",
                table: "message_attachments",
                columns: new[] { "tenant_id", "message_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "message_attachments",
                schema: "conversations");
        }
    }
}
