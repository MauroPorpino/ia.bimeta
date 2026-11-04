using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.Conversations.Infrastructure.Migrations
{
    /// <summary>
    /// Fatia A3 do MVP (migração 6 reduzida do ADR 0011): a mensagem passa a guardar o bloco opaco de metadados do canal (<c>ChannelData</c>), que o Conversations não interpreta.
    /// Só o que a A3 precisa: <c>channel_data_version</c> e <c>channel_data</c> em <c>messages</c>. Os anexos, <c>outbound_operation_id</c> e <c>outbound_request_hash</c> da migração 6
    /// completa chegam com a fatia que os usa (a resposta humana, A4).
    ///
    /// Efeitos de <c>Up</c> (uma transação):
    ///   1. <c>channel_data_version integer NULL</c> e <c>channel_data jsonb NULL</c>. Colunas anuláveis e sem valor padrão: as mensagens existentes ficam sem bloco (nulo nos dois),
    ///      nenhuma linha é reescrita, e o código anterior (que não conhece as colunas) continua inserindo normalmente.
    ///   2. <c>ck_messages_channel_data</c>: a versão e os pares andam juntos (os dois nulos, ou a versão de 1 em diante com um objeto JSON). As linhas existentes (nulo e nulo) a satisfazem.
    ///
    /// Plano de reversão (<c>Down</c>): remove a restrição e as duas colunas. Antes de remover qualquer coisa, VERIFICA se isso perderia dado (alguma mensagem com bloco) e, havendo,
    /// ABORTA com mensagem clara, sem alterar nada: o bloco do canal não tem cópia em outro lugar, e nunca é descartado em silêncio. Para reverter de fato, exporte e limpe os dados de
    /// forma explícita e consciente antes (por exemplo, depois de um backup, <c>UPDATE conversations.messages SET channel_data = NULL, channel_data_version = NULL</c>).
    /// </summary>
    public partial class MessageChannelData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "channel_data",
                schema: "conversations",
                table: "messages",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "channel_data_version",
                schema: "conversations",
                table: "messages",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_messages_channel_data",
                schema: "conversations",
                table: "messages",
                sql: "(channel_data IS NULL AND channel_data_version IS NULL) OR (channel_data IS NOT NULL AND channel_data_version IS NOT NULL AND channel_data_version >= 1 AND jsonb_typeof(channel_data) = 'object')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Verificação ANTES de qualquer mudança: a reversão não descarta dado. Se houver mensagem com bloco de metadados do canal, a transação aborta aqui.
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM conversations.messages WHERE channel_data IS NOT NULL OR channel_data_version IS NOT NULL) THEN
        RAISE EXCEPTION 'Reversão recusada: há mensagens com metadados do canal (channel_data); remover as colunas os perderia. Exporte e limpe esses dados de forma explícita antes.';
    END IF;
END
$$;");

            migrationBuilder.DropCheckConstraint(
                name: "ck_messages_channel_data",
                schema: "conversations",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "channel_data",
                schema: "conversations",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "channel_data_version",
                schema: "conversations",
                table: "messages");
        }
    }
}
