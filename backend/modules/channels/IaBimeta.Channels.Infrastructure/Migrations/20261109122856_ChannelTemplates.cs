using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.Channels.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ChannelTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "channel_templates",
                schema: "channels",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    language = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    category = table.Column<short>(type: "smallint", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    meta_template_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    header_text = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    body_text = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    footer_text = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    parameters = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_channel_templates", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_channel_templates_body", "char_length(body_text) BETWEEN 1 AND 1024");
                    table.CheckConstraint("ck_channel_templates_category", "category IN (1, 2)");
                    table.CheckConstraint("ck_channel_templates_status", "status IN (1, 2, 3, 4, 5)");
                    table.ForeignKey(
                        name: "FK_channel_templates_channel_accounts_tenant_id_account_id",
                        columns: x => new { x.tenant_id, x.account_id },
                        principalSchema: "channels",
                        principalTable: "channel_accounts",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_channel_templates_tenant_id_account_id_name_language",
                schema: "channels",
                table: "channel_templates",
                columns: new[] { "tenant_id", "account_id", "name", "language" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "channel_templates",
                schema: "channels");
        }
    }
}
