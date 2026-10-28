using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.Conversations.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddChannelDefaultLocaleSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_conversations_locale",
                schema: "conversations",
                table: "conversations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_conversations_locale",
                schema: "conversations",
                table: "conversations",
                sql: "effective_locale ~ '^[A-Za-z]{2,8}(-[A-Za-z0-9]{1,8})*$' AND locale_source IN (1, 2, 3, 4, 5, 6)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_conversations_locale",
                schema: "conversations",
                table: "conversations");

            // Plano de reversão: a origem "idioma padrão do canal" (6) deixa de existir. As conversas que a usam voltam a ser
            // uma suposição de último recurso ("padrão do tenant", 1); o idioma efetivo em si não muda.
            migrationBuilder.Sql("UPDATE conversations.conversations SET locale_source = 1 WHERE locale_source = 6;");

            migrationBuilder.AddCheckConstraint(
                name: "ck_conversations_locale",
                schema: "conversations",
                table: "conversations",
                sql: "effective_locale ~ '^[A-Za-z]{2,8}(-[A-Za-z0-9]{1,8})*$' AND locale_source IN (1, 2, 3, 4, 5)");
        }
    }
}
