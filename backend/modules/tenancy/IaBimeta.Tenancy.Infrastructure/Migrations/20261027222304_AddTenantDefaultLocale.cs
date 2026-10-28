using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.Tenancy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantDefaultLocale : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "default_locale",
                schema: "tenancy",
                table: "tenants",
                type: "character varying(35)",
                maxLength: 35,
                nullable: false,
                defaultValue: "pt-BR");

            // Os tenants que já existiam recebem o idioma inicial da plataforma (Tenant.FallbackDefaultLocale). O padrão do
            // banco é só para preencher essas linhas: depois ele sai, e todo tenant novo traz o idioma explicitamente.
            migrationBuilder.Sql("ALTER TABLE tenancy.tenants ALTER COLUMN default_locale DROP DEFAULT;");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tenants_default_locale",
                schema: "tenancy",
                table: "tenants",
                sql: "default_locale ~ '^[A-Za-z]{2,8}(-[A-Za-z0-9]{1,8})*$'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_tenants_default_locale",
                schema: "tenancy",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "default_locale",
                schema: "tenancy",
                table: "tenants");
        }
    }
}
