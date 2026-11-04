using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.Conversations.Infrastructure.Migrations
{
    /// <summary>
    /// Fatia A2 do MVP (ADR 0012, seção A, itens 1 e 6): a sessão passa a declarar COMO se associa (<c>association_mode</c>, hoje só 1 = contato) e a ter uma chave de
    /// thread reservada (<c>thread_key</c>, hoje sempre nula); a unicidade da sessão aberta de chat troca de índice; e dois limites são alargados.
    ///
    /// Efeitos, na ordem em que ocorrem em <c>Up</c> (uma transação; o EF aplica cada migração numa):
    ///   1. <c>association_mode smallint NOT NULL DEFAULT 1</c> e <c>thread_key text NULL</c> em <c>service_sessions</c>. O valor padrão constante é só metadados no PostgreSQL 11 ou
    ///      superior (sem reescrever a tabela) e faz as linhas existentes, e quem insere sem informar a coluna, ficarem como sessão de chat.
    ///   2. <c>messages.provider_event_id</c> de varchar(200) para varchar(512) e <c>contacts.external_ref</c> de varchar(128) para varchar(254). Alargar um varchar não reescreve a
    ///      tabela nem os índices (que seguem válidos); nenhum dado muda.
    ///   3. <c>ck_service_sessions_association</c>: <c>association_mode = 1 AND thread_key IS NULL</c> (estrito de propósito: a fatia da thread de e-mail o substitui).
    ///   4. O índice <c>ux_service_sessions_open_chat</c> é CRIADO ANTES de o antigo (<c>ix_service_sessions_one_open_per_contact</c>) ser removido: em nenhum instante há sessão
    ///      aberta de chat sem índice único. As sessões existentes têm <c>thread_key</c> nulo, então os dois índices cobrem o mesmo conjunto e o comportamento delas não muda.
    ///
    /// Compatibilidade com o código: o <c>ON CONFLICT</c> da criação de sessão passou a repetir o alvo e o predicado do índice novo
    /// (<c>(tenant_id, contact_id) WHERE status = 1 AND thread_key IS NULL</c>). O código ANTERIOR (<c>WHERE status = 1</c>) NÃO encontra índice que o sustente neste banco e falha de forma
    /// ruidosa (PostgreSQL 42P10), nunca em silêncio; e o código NOVO falha de forma ruidosa num banco sem a migração (colunas inexistentes). Por isso: aplicar a migração com o
    /// aplicativo parado e subir o código novo em seguida; nunca rodar versões misturadas contra o mesmo banco.
    ///
    /// Plano de reversão (<c>Down</c>): recria o índice antigo ANTES de remover o novo, remove a restrição e as duas colunas e estreita os dois varchar de volta. Antes de
    /// estreitar ou remover qualquer coisa, VERIFICA se isso perderia dado (valor maior que o limite antigo, sessão que não seja de chat) e, havendo, ABORTA com mensagem clara,
    /// sem alterar nada: o dado novo nunca é truncado nem descartado em silêncio. Aplicar a reversão com o aplicativo (versão anterior) ainda parado.
    /// </summary>
    public partial class ChatSessionAssociationAndLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<short>(
                name: "association_mode",
                schema: "conversations",
                table: "service_sessions",
                type: "smallint",
                nullable: false,
                defaultValue: (short)1);

            migrationBuilder.AddColumn<string>(
                name: "thread_key",
                schema: "conversations",
                table: "service_sessions",
                type: "text",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "provider_event_id",
                schema: "conversations",
                table: "messages",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "external_ref",
                schema: "conversations",
                table: "contacts",
                type: "character varying(254)",
                maxLength: 254,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128);

            migrationBuilder.AddCheckConstraint(
                name: "ck_service_sessions_association",
                schema: "conversations",
                table: "service_sessions",
                sql: "association_mode = 1 AND thread_key IS NULL");

            // O índice novo ANTES da remoção do antigo.
            migrationBuilder.CreateIndex(
                name: "ux_service_sessions_open_chat",
                schema: "conversations",
                table: "service_sessions",
                columns: new[] { "tenant_id", "contact_id" },
                unique: true,
                filter: "status = 1 AND thread_key IS NULL");

            migrationBuilder.DropIndex(
                name: "ix_service_sessions_one_open_per_contact",
                schema: "conversations",
                table: "service_sessions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Verificações ANTES de qualquer mudança: a reversão não trunca nem descarta dado. Se algum valor não couber no limite antigo, ou se houver sessão que não
            // seja de chat (só possível se uma migração posterior tiver relaxado a restrição e já tiver sido revertida de forma incompleta), a transação aborta aqui.
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM conversations.contacts WHERE length(external_ref) > 128) THEN
        RAISE EXCEPTION 'Reversão recusada: há contatos com external_ref maior que 128 caracteres; estreitar a coluna os truncaria.';
    END IF;

    IF EXISTS (SELECT 1 FROM conversations.messages WHERE length(provider_event_id) > 200) THEN
        RAISE EXCEPTION 'Reversão recusada: há mensagens com provider_event_id maior que 200 caracteres; estreitar a coluna as truncaria.';
    END IF;

    IF EXISTS (SELECT 1 FROM conversations.service_sessions WHERE association_mode <> 1 OR thread_key IS NOT NULL) THEN
        RAISE EXCEPTION 'Reversão recusada: há sessões que não são de chat (association_mode diferente de 1 ou thread_key preenchido); remover as colunas as perderia.';
    END IF;
END
$$;");

            // O índice antigo volta ANTES de o novo sair. Como toda sessão é de chat (verificado acima), os dois cobrem o mesmo conjunto.
            migrationBuilder.CreateIndex(
                name: "ix_service_sessions_one_open_per_contact",
                schema: "conversations",
                table: "service_sessions",
                columns: new[] { "tenant_id", "contact_id" },
                unique: true,
                filter: "status = 1");

            migrationBuilder.DropIndex(
                name: "ux_service_sessions_open_chat",
                schema: "conversations",
                table: "service_sessions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_service_sessions_association",
                schema: "conversations",
                table: "service_sessions");

            migrationBuilder.DropColumn(
                name: "association_mode",
                schema: "conversations",
                table: "service_sessions");

            migrationBuilder.DropColumn(
                name: "thread_key",
                schema: "conversations",
                table: "service_sessions");

            migrationBuilder.AlterColumn<string>(
                name: "provider_event_id",
                schema: "conversations",
                table: "messages",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(512)",
                oldMaxLength: 512,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "external_ref",
                schema: "conversations",
                table: "contacts",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(254)",
                oldMaxLength: 254);
        }
    }
}
