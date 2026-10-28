using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.Channels.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "channels");

            migrationBuilder.CreateTable(
                name: "channel_accounts",
                schema: "channels",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<short>(type: "smallint", nullable: false),
                    provider_account_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_channel_accounts", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_channel_accounts_provider", "provider IN (1)");
                });

            migrationBuilder.CreateTable(
                name: "channel_instances",
                schema: "channels",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<short>(type: "smallint", nullable: false),
                    phone_number_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    display_phone_number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    default_locale = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: true),
                    default_business_unit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    default_department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_channel_instances", x => new { x.tenant_id, x.id });
                    table.UniqueConstraint("ak_channel_instances_route_key", x => new { x.tenant_id, x.id, x.provider, x.phone_number_id });
                    table.CheckConstraint("ck_channel_instances_default_department", "default_department_id IS NULL OR default_business_unit_id IS NOT NULL");
                    table.CheckConstraint("ck_channel_instances_default_locale", "default_locale IS NULL OR default_locale ~ '^[A-Za-z]{2,8}(-[A-Za-z0-9]{1,8})*$'");
                    table.CheckConstraint("ck_channel_instances_provider", "provider IN (1)");
                    table.CheckConstraint("ck_channel_instances_status", "status IN (1, 2, 3)");
                    table.CheckConstraint("ck_channel_instances_version", "version >= 1");
                    table.ForeignKey(
                        name: "FK_channel_instances_channel_accounts_tenant_id_account_id",
                        columns: x => new { x.tenant_id, x.account_id },
                        principalSchema: "channels",
                        principalTable: "channel_accounts",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "channel_secrets",
                schema: "channels",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    key_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ciphertext = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_channel_secrets", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_channel_secrets_ciphertext", "char_length(key_id) >= 1 AND char_length(ciphertext) BETWEEN 1 AND 16384");
                    table.CheckConstraint("ck_channel_secrets_kind", "kind IN (1, 2, 3)");
                    table.CheckConstraint("ck_channel_secrets_version", "version >= 1");
                    table.ForeignKey(
                        name: "FK_channel_secrets_channel_accounts_tenant_id_account_id",
                        columns: x => new { x.tenant_id, x.account_id },
                        principalSchema: "channels",
                        principalTable: "channel_accounts",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "channel_instance_units",
                schema: "channels",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    authorized_by = table.Column<Guid>(type: "uuid", nullable: true),
                    authorized_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_channel_instance_units", x => new { x.tenant_id, x.channel_instance_id, x.business_unit_id });
                    table.ForeignKey(
                        name: "FK_channel_instance_units_channel_instances_tenant_id_channel_~",
                        columns: x => new { x.tenant_id, x.channel_instance_id },
                        principalSchema: "channels",
                        principalTable: "channel_instances",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "channel_routes",
                schema: "channels",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<short>(type: "smallint", nullable: false),
                    external_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    channel_instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_channel_routes", x => new { x.tenant_id, x.provider, x.external_id });
                    table.CheckConstraint("ck_channel_routes_provider", "provider IN (1)");
                    table.ForeignKey(
                        name: "FK_channel_routes_channel_instances_tenant_id_channel_instance~",
                        columns: x => new { x.tenant_id, x.channel_instance_id, x.provider, x.external_id },
                        principalSchema: "channels",
                        principalTable: "channel_instances",
                        principalColumns: new[] { "tenant_id", "id", "provider", "phone_number_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_channel_accounts_tenant_id_organization_id",
                schema: "channels",
                table: "channel_accounts",
                columns: new[] { "tenant_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ux_channel_accounts_provider_account_id",
                schema: "channels",
                table: "channel_accounts",
                columns: new[] { "provider", "provider_account_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_channel_instance_units_tenant_id_business_unit_id",
                schema: "channels",
                table: "channel_instance_units",
                columns: new[] { "tenant_id", "business_unit_id" });

            migrationBuilder.CreateIndex(
                name: "IX_channel_instances_tenant_id_account_id",
                schema: "channels",
                table: "channel_instances",
                columns: new[] { "tenant_id", "account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_channel_instances_tenant_id_organization_id",
                schema: "channels",
                table: "channel_instances",
                columns: new[] { "tenant_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ux_channel_instances_provider_phone_number_id",
                schema: "channels",
                table: "channel_instances",
                columns: new[] { "provider", "phone_number_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_channel_routes_tenant_id_channel_instance_id_provider_exter~",
                schema: "channels",
                table: "channel_routes",
                columns: new[] { "tenant_id", "channel_instance_id", "provider", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_channel_routes_provider_external_id",
                schema: "channels",
                table: "channel_routes",
                columns: new[] { "provider", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_channel_secrets_tenant_id_account_id_kind",
                schema: "channels",
                table: "channel_secrets",
                columns: new[] { "tenant_id", "account_id", "kind" },
                unique: true);

            // A unidade padrão do canal precisa estar entre as autorizadas: garantido pelo banco, além do domínio. A chave é
            // ADIADA até o fim da transação porque instância e unidades são gravadas juntas (a ordem das instruções não importa)
            // e a referência é circular com a das unidades à instância. Com a unidade padrão nula nada é conferido. Fica só
            // em SQL (o modelo do EF não a conhece, para não criar dependência circular na gravação).
            migrationBuilder.Sql(
                """
                ALTER TABLE channels.channel_instances
                    ADD CONSTRAINT fk_channel_instances_default_unit_authorized
                    FOREIGN KEY (tenant_id, id, default_business_unit_id)
                    REFERENCES channels.channel_instance_units (tenant_id, channel_instance_id, business_unit_id)
                    DEFERRABLE INITIALLY DEFERRED;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE channels.channel_instances DROP CONSTRAINT fk_channel_instances_default_unit_authorized;");

            migrationBuilder.DropTable(
                name: "channel_instance_units",
                schema: "channels");

            migrationBuilder.DropTable(
                name: "channel_routes",
                schema: "channels");

            migrationBuilder.DropTable(
                name: "channel_secrets",
                schema: "channels");

            migrationBuilder.DropTable(
                name: "channel_instances",
                schema: "channels");

            migrationBuilder.DropTable(
                name: "channel_accounts",
                schema: "channels");
        }
    }
}
