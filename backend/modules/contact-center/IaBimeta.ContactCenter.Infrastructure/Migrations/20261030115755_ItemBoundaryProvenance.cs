using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.ContactCenter.Infrastructure.Migrations
{
    /// <summary>
    /// Proveniência das fronteiras de histórico do item. ADITIVA e reversível (o <c>Down</c> remove as duas colunas e a restrição) e SEM reescrita de dados:
    /// todo item existente fica em <c>boundary_model = 0</c> (legado), e fronteiras positivas sem prova de origem preservam o número como metadado, mas deixam de
    /// autorizar conteúdo. Nenhum registro é promovido a 1 por valores positivos, nem por <c>operation_id</c> ou histórico: isso não prova a colocação autorizada.
    /// Qualquer revalidação de registro existente é uma etapa própria, revisada e aprovada à parte, e NÃO faz parte desta migração.
    /// </summary>
    public partial class ItemBoundaryProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<short>(
                name: "boundary_model",
                schema: "contact_center",
                table: "queue_items",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<long>(
                name: "history_limit_origin_epoch",
                schema: "contact_center",
                table: "queue_items",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_queue_items_boundary_provenance",
                schema: "contact_center",
                table: "queue_items",
                sql: "boundary_model IN (0, 1) AND (history_limit_origin_epoch IS NULL OR (history_limit_origin_epoch >= 1 AND history_limit_seq IS NOT NULL AND boundary_model = 1))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_queue_items_boundary_provenance",
                schema: "contact_center",
                table: "queue_items");

            migrationBuilder.DropColumn(
                name: "boundary_model",
                schema: "contact_center",
                table: "queue_items");

            migrationBuilder.DropColumn(
                name: "history_limit_origin_epoch",
                schema: "contact_center",
                table: "queue_items");
        }
    }
}
