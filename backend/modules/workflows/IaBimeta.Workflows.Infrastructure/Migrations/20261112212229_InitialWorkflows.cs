using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.Workflows.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialWorkflows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "workflows");

            migrationBuilder.CreateTable(
                name: "workflow_versions",
                schema: "workflows",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    graph_json = table.Column<string>(type: "text", nullable: false),
                    graph_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    published_by = table.Column<Guid>(type: "uuid", nullable: true),
                    retired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_versions", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_workflow_versions_graph", "octet_length(graph_json) <= 4194304");
                    table.CheckConstraint("ck_workflow_versions_published", "(status = 1) = (published_at IS NULL)");
                    table.CheckConstraint("ck_workflow_versions_retired", "(status = 3) = (retired_at IS NOT NULL)");
                    table.CheckConstraint("ck_workflow_versions_status", "status IN (1, 2, 3)");
                    table.CheckConstraint("ck_workflow_versions_version", "version >= 1 AND revision >= 1");
                });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_versions_tenant_id_organization_id",
                schema: "workflows",
                table: "workflow_versions",
                columns: new[] { "tenant_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ux_workflow_versions_draft",
                schema: "workflows",
                table: "workflow_versions",
                columns: new[] { "tenant_id", "key", "status" },
                unique: true,
                filter: "status = 1");

            migrationBuilder.CreateIndex(
                name: "ux_workflow_versions_published",
                schema: "workflows",
                table: "workflow_versions",
                columns: new[] { "tenant_id", "key" },
                unique: true,
                filter: "status = 2");

            migrationBuilder.CreateIndex(
                name: "ux_workflow_versions_version",
                schema: "workflows",
                table: "workflow_versions",
                columns: new[] { "tenant_id", "key", "version" },
                unique: true);

            // ADR 0037 §6: uma versão publicada é IMUTÁVEL. A regra vale também no banco (um gatilho recusa qualquer alteração do conteúdo de uma versão que não é rascunho, a volta de aposentada
            // e a exclusão de versão que não é rascunho), então nem uma falha de aplicação nem um acesso direto a muda.
            migrationBuilder.Sql(@"
CREATE FUNCTION workflows.workflow_versions_guard() RETURNS trigger AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        IF OLD.status <> 1 THEN
            RAISE EXCEPTION 'workflow version is immutable' USING ERRCODE = '23514';
        END IF;
        RETURN OLD;
    END IF;
    IF OLD.status <> 1 THEN
        IF NEW.graph_json IS DISTINCT FROM OLD.graph_json OR NEW.graph_hash IS DISTINCT FROM OLD.graph_hash
           OR NEW.name IS DISTINCT FROM OLD.name OR NEW.description IS DISTINCT FROM OLD.description
           OR NEW.key <> OLD.key OR NEW.version <> OLD.version OR NEW.organization_id <> OLD.organization_id
           OR NEW.tenant_id <> OLD.tenant_id OR NEW.published_at IS DISTINCT FROM OLD.published_at THEN
            RAISE EXCEPTION 'workflow version is immutable' USING ERRCODE = '23514';
        END IF;
        IF NEW.status < OLD.status THEN
            RAISE EXCEPTION 'workflow version status cannot go back' USING ERRCODE = '23514';
        END IF;
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_workflow_versions_guard
    BEFORE UPDATE OR DELETE ON workflows.workflow_versions
    FOR EACH ROW EXECUTE FUNCTION workflows.workflow_versions_guard();
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_workflow_versions_guard ON workflows.workflow_versions; DROP FUNCTION IF EXISTS workflows.workflow_versions_guard();");

            migrationBuilder.DropTable(
                name: "workflow_versions",
                schema: "workflows");
        }
    }
}
