using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantUserPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenant_user_policies",
                schema: "identity",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    max_users = table.Column<int>(type: "integer", nullable: true),
                    registration_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_user_policies", x => x.tenant_id);
                    table.CheckConstraint("ck_tenant_user_policies_max_users", "max_users IS NULL OR max_users >= 1");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenant_user_policies",
                schema: "identity");
        }
    }
}
