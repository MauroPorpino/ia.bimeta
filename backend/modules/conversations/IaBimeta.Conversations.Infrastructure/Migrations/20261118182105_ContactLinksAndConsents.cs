using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace IaBimeta.Conversations.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ContactLinksAndConsents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "contact_consents",
                schema: "conversations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    phone = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    purpose = table.Column<short>(type: "smallint", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    origin = table.Column<short>(type: "smallint", nullable: false),
                    evidence = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    collected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_kind = table.Column<short>(type: "smallint", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    customer_ref = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_message_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contact_consents", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_contact_consents_customer_ref", "customer_ref IS NULL OR char_length(customer_ref) BETWEEN 1 AND 128");
                    table.CheckConstraint("ck_contact_consents_domain", "purpose IN (1, 2) AND status IN (1, 2) AND origin IN (1, 2, 3) AND actor_kind IN (1, 2, 3)");
                    table.CheckConstraint("ck_contact_consents_evidence", "char_length(btrim(evidence)) BETWEEN 1 AND 2000");
                    table.CheckConstraint("ck_contact_consents_origin_actor", "(origin = 1 AND actor_kind = 1 AND actor_id IS NOT NULL) OR (origin = 2 AND actor_kind = 2 AND actor_id IS NOT NULL) OR (origin = 3 AND actor_kind = 3 AND actor_id IS NULL AND session_id IS NOT NULL AND source_message_id IS NOT NULL)");
                    table.CheckConstraint("ck_contact_consents_phone", "phone ~ '^\\+[1-9][0-9]{7,14}$'");
                });

            migrationBuilder.CreateTable(
                name: "contact_external_links",
                schema: "conversations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_ref = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    contact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    origin = table.Column<short>(type: "smallint", nullable: false),
                    linked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    linked_by_kind = table.Column<short>(type: "smallint", nullable: false),
                    linked_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unlinked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    unlinked_by_kind = table.Column<short>(type: "smallint", nullable: true),
                    unlinked_by_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contact_external_links", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_contact_external_links_customer_ref", "char_length(customer_ref) BETWEEN 1 AND 128");
                    table.CheckConstraint("ck_contact_external_links_origin", "origin IN (1, 2) AND linked_by_kind IN (1, 2) AND ((origin = 1) = (linked_by_kind = 1))");
                    table.CheckConstraint("ck_contact_external_links_unlink", "(unlinked_at IS NULL AND unlinked_by_kind IS NULL AND unlinked_by_id IS NULL) OR (unlinked_at IS NOT NULL AND unlinked_by_kind IN (1, 2) AND unlinked_by_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_contact_external_links_contacts_tenant_id_contact_id",
                        columns: x => new { x.tenant_id, x.contact_id },
                        principalSchema: "conversations",
                        principalTable: "contacts",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            // Somente acréscimo (ADR 0041, seção 4): o banco recusa UPDATE e DELETE de qualquer linha de consentimento, além de o aplicativo não ter caminho para isso. TRUNCATE não passa por este gatilho de
            // propósito (é operação de manutenção do banco, nunca do aplicativo). O Down remove o gatilho junto com a tabela e depois a função.
            migrationBuilder.Sql(@"CREATE FUNCTION conversations.contact_consents_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'contact_consents e somente de acrescimo (ADR 0041).' USING ERRCODE = '23000';
END;
$$;");
            migrationBuilder.Sql(@"CREATE TRIGGER tr_contact_consents_append_only BEFORE UPDATE OR DELETE ON conversations.contact_consents
FOR EACH ROW EXECUTE FUNCTION conversations.contact_consents_append_only();");

            migrationBuilder.CreateIndex(
                name: "ix_contact_consents_tenant_id_organization_id_phone_purpose_seq",
                schema: "conversations",
                table: "contact_consents",
                columns: new[] { "tenant_id", "organization_id", "phone", "purpose", "seq" });

            migrationBuilder.CreateIndex(
                name: "ux_contact_consents_keyword_source",
                schema: "conversations",
                table: "contact_consents",
                columns: new[] { "tenant_id", "source_message_id", "purpose" },
                unique: true,
                filter: "origin = 3");

            migrationBuilder.CreateIndex(
                name: "ix_contact_external_links_tenant_id_connection_id_customer_ref",
                schema: "conversations",
                table: "contact_external_links",
                columns: new[] { "tenant_id", "connection_id", "customer_ref" });

            migrationBuilder.CreateIndex(
                name: "ix_contact_external_links_tenant_id_contact_id",
                schema: "conversations",
                table: "contact_external_links",
                columns: new[] { "tenant_id", "contact_id" });

            migrationBuilder.CreateIndex(
                name: "ux_contact_external_links_active",
                schema: "conversations",
                table: "contact_external_links",
                columns: new[] { "tenant_id", "connection_id", "customer_ref", "contact_id" },
                unique: true,
                filter: "unlinked_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Plano de rollback: as duas tabelas são novas e aditivas (nenhuma coluna existente foi alterada), então reverter é remover o gatilho, as tabelas e a função. Os consentimentos gravados SE PERDEM: antes de
            // reverter em um banco com dados reais, exporte a tabela (ela é registro de referência, ADR 0041); a reversão só é segura antes de qualquer uso das rotas da E2.2.
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS tr_contact_consents_append_only ON conversations.contact_consents;");

            migrationBuilder.DropTable(
                name: "contact_consents",
                schema: "conversations");

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS conversations.contact_consents_append_only();");

            migrationBuilder.DropTable(
                name: "contact_external_links",
                schema: "conversations");
        }
    }
}
