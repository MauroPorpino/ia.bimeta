using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.Conversations.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class OptOutReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "optout_reviews",
                schema: "conversations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<short>(type: "smallint", nullable: false),
                    keyword = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolution = table.Column<short>(type: "smallint", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolved_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_optout_reviews", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_optout_reviews_keyword", "keyword IS NULL OR char_length(keyword) BETWEEN 1 AND 32");
                    table.CheckConstraint("ck_optout_reviews_reason", "reason IN (1, 2, 3)");
                    table.CheckConstraint("ck_optout_reviews_resolution", "(resolution IS NULL AND resolved_at IS NULL AND resolved_by IS NULL) OR (resolution IN (1, 2) AND resolved_at IS NOT NULL AND resolved_by IS NOT NULL)");
                });

            // Resolvida UMA única vez (ADR 0041, seção 5): o banco recusa o DELETE de qualquer linha, o UPDATE de uma linha já resolvida e o UPDATE que mude qualquer coisa além da resolução (decisão, instante e
            // responsável). É a última barreira contra duas decisões conflitantes para a mesma ocorrência, além da trava e da condição do aplicativo.
            migrationBuilder.Sql(@"CREATE FUNCTION conversations.optout_reviews_resolve_once() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'optout_reviews nao admite exclusao (ADR 0041).' USING ERRCODE = '23000';
    END IF;
    IF OLD.resolution IS NOT NULL THEN
        RAISE EXCEPTION 'a ocorrencia ja foi resolvida e nao muda mais (ADR 0041).' USING ERRCODE = '23000';
    END IF;
    IF NEW.id <> OLD.id OR NEW.tenant_id <> OLD.tenant_id OR NEW.organization_id <> OLD.organization_id OR NEW.session_id <> OLD.session_id
       OR NEW.message_id <> OLD.message_id OR NEW.reason <> OLD.reason OR NEW.keyword IS DISTINCT FROM OLD.keyword OR NEW.opened_at <> OLD.opened_at THEN
        RAISE EXCEPTION 'so a resolucao da ocorrencia pode ser gravada (ADR 0041).' USING ERRCODE = '23000';
    END IF;
    RETURN NEW;
END;
$$;");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_optout_reviews_resolve_once BEFORE UPDATE OR DELETE ON conversations.optout_reviews
FOR EACH ROW EXECUTE FUNCTION conversations.optout_reviews_resolve_once();");

            migrationBuilder.CreateIndex(
                name: "ix_optout_reviews_tenant_id_session_id_opened_at",
                schema: "conversations",
                table: "optout_reviews",
                columns: new[] { "tenant_id", "session_id", "opened_at" });

            migrationBuilder.CreateIndex(
                name: "ux_optout_reviews_message",
                schema: "conversations",
                table: "optout_reviews",
                columns: new[] { "tenant_id", "message_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Plano de rollback: a tabela é nova e aditiva. Reverter remove o gatilho, a tabela e a função; as decisões sobre possíveis saídas gravadas SE PERDEM (exportar antes em banco com dados reais; os
            // consentimentos revogados por elas, em contact_consents, permanecem).
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS tr_optout_reviews_resolve_once ON conversations.optout_reviews;");

            migrationBuilder.DropTable(
                name: "optout_reviews",
                schema: "conversations");

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS conversations.optout_reviews_resolve_once();");
        }
    }
}
