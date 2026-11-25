using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserStatusAndPermissionOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "status",
                schema: "identity",
                table: "users",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Active");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "status_changed_at",
                schema: "identity",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "user_permission_overrides",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    permission = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    effect = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_permission_overrides", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "FK_user_permission_overrides_users_tenant_id_user_id",
                        columns: x => new { x.tenant_id, x.user_id },
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_user_permission_overrides_tenant_user_scope_permission",
                schema: "identity",
                table: "user_permission_overrides",
                columns: new[] { "tenant_id", "user_id", "organization_id", "permission" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_permission_overrides",
                schema: "identity");

            migrationBuilder.DropColumn(
                name: "status",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "status_changed_at",
                schema: "identity",
                table: "users");
        }
    }
}
