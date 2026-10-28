using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.Conversations.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLocaleFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "locale",
                schema: "conversations",
                table: "messages",
                type: "character varying(35)",
                maxLength: 35,
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "locale_origin",
                schema: "conversations",
                table: "messages",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "effective_locale",
                schema: "conversations",
                table: "conversations",
                type: "character varying(35)",
                maxLength: 35,
                nullable: false,
                defaultValue: "pt-BR");

            migrationBuilder.AddColumn<short>(
                name: "locale_source",
                schema: "conversations",
                table: "conversations",
                type: "smallint",
                nullable: false,
                defaultValue: (short)1);

            // As conversas que já existiam não têm idioma registrado: recebem o idioma inicial da plataforma, marcado como
            // SUPOSIÇÃO (origem 1 = TenantDefault), que nenhuma informação melhor perde para ele. Os padrões do banco são só
            // para preencher essas linhas: depois saem, e toda conversa nova traz idioma e origem explicitamente.
            migrationBuilder.Sql("ALTER TABLE conversations.conversations ALTER COLUMN effective_locale DROP DEFAULT;");
            migrationBuilder.Sql("ALTER TABLE conversations.conversations ALTER COLUMN locale_source DROP DEFAULT;");

            migrationBuilder.AddColumn<string>(
                name: "preferred_locale",
                schema: "conversations",
                table: "contacts",
                type: "character varying(35)",
                maxLength: 35,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "message_translations",
                schema: "conversations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_locale = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: false),
                    source_locale = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: true),
                    text = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    provenance = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_message_translations", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_message_translations_locales", "target_locale ~ '^[A-Za-z]{2,8}(-[A-Za-z0-9]{1,8})*$' AND (source_locale IS NULL OR source_locale ~ '^[A-Za-z]{2,8}(-[A-Za-z0-9]{1,8})*$')");
                    table.ForeignKey(
                        name: "FK_message_translations_messages_tenant_id_message_id",
                        columns: x => new { x.tenant_id, x.message_id },
                        principalSchema: "conversations",
                        principalTable: "messages",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_messages_locale",
                schema: "conversations",
                table: "messages",
                sql: "(locale IS NULL AND locale_origin IS NULL) OR (locale IS NOT NULL AND locale_origin IS NOT NULL AND locale_origin IN (1, 2) AND locale ~ '^[A-Za-z]{2,8}(-[A-Za-z0-9]{1,8})*$')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_conversations_locale",
                schema: "conversations",
                table: "conversations",
                sql: "effective_locale ~ '^[A-Za-z]{2,8}(-[A-Za-z0-9]{1,8})*$' AND locale_source IN (1, 2, 3, 4, 5)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_contacts_preferred_locale",
                schema: "conversations",
                table: "contacts",
                sql: "preferred_locale IS NULL OR preferred_locale ~ '^[A-Za-z]{2,8}(-[A-Za-z0-9]{1,8})*$'");

            migrationBuilder.CreateIndex(
                name: "ix_message_translations_tenant_id_message_id_target_locale",
                schema: "conversations",
                table: "message_translations",
                columns: new[] { "tenant_id", "message_id", "target_locale" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "message_translations",
                schema: "conversations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_messages_locale",
                schema: "conversations",
                table: "messages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_conversations_locale",
                schema: "conversations",
                table: "conversations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_contacts_preferred_locale",
                schema: "conversations",
                table: "contacts");

            migrationBuilder.DropColumn(
                name: "locale",
                schema: "conversations",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "locale_origin",
                schema: "conversations",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "effective_locale",
                schema: "conversations",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "locale_source",
                schema: "conversations",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "preferred_locale",
                schema: "conversations",
                table: "contacts");
        }
    }
}
