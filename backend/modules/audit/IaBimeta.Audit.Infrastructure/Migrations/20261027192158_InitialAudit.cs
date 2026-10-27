using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.Audit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "audit");

            migrationBuilder.CreateTable(
                name: "audit_events",
                schema: "audit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_origin = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    operation = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    resource_type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    resource_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    outcome = table.Column<short>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => new { x.tenant_id, x.id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_tenant_id_occurred_at",
                schema: "audit",
                table: "audit_events",
                columns: new[] { "tenant_id", "occurred_at" });

            // Trilha append-only garantida pelo banco: alterar, excluir ou truncar eventos é recusado, mesmo por
            // quem contornar o aplicativo.
            migrationBuilder.Sql(@"
CREATE FUNCTION audit.reject_audit_change() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'A trilha de auditoria so aceita inclusoes: % em audit.audit_events nao e permitido.', TG_OP
        USING ERRCODE = 'restrict_violation';
END;
$$;");

            migrationBuilder.Sql(
                "CREATE TRIGGER trg_audit_events_append_only BEFORE UPDATE OR DELETE ON audit.audit_events " +
                "FOR EACH ROW EXECUTE FUNCTION audit.reject_audit_change();");

            migrationBuilder.Sql(
                "CREATE TRIGGER trg_audit_events_no_truncate BEFORE TRUNCATE ON audit.audit_events " +
                "FOR EACH STATEMENT EXECUTE FUNCTION audit.reject_audit_change();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_events",
                schema: "audit");

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS audit.reject_audit_change();");
        }
    }
}
