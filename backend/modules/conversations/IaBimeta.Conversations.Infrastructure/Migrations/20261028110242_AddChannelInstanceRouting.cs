using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.Conversations.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddChannelInstanceRouting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "channel_id",
                schema: "conversations",
                table: "service_sessions",
                newName: "channel_instance_id");

            migrationBuilder.RenameColumn(
                name: "channel_id",
                schema: "conversations",
                table: "messages",
                newName: "channel_instance_id");

            migrationBuilder.RenameIndex(
                name: "ix_messages_tenant_id_channel_id_provider_event_id",
                schema: "conversations",
                table: "messages",
                newName: "ix_messages_tenant_id_channel_instance_id_provider_event_id");

            migrationBuilder.RenameColumn(
                name: "channel_id",
                schema: "conversations",
                table: "conversations",
                newName: "channel_instance_id");

            migrationBuilder.RenameIndex(
                name: "ix_conversations_tenant_id_session_id_channel_id",
                schema: "conversations",
                table: "conversations",
                newName: "ix_conversations_tenant_id_session_id_channel_instance_id");

            migrationBuilder.RenameColumn(
                name: "channel_id",
                schema: "conversations",
                table: "contacts",
                newName: "channel_instance_id");

            migrationBuilder.RenameIndex(
                name: "ix_contacts_tenant_id_channel_id_external_ref",
                schema: "conversations",
                table: "contacts",
                newName: "ix_contacts_tenant_id_channel_instance_id_external_ref");

            migrationBuilder.AddColumn<Guid>(
                name: "department_id",
                schema: "conversations",
                table: "service_sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "session_transfers",
                schema: "conversations",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transfer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_business_unit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    from_department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    to_business_unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    to_department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    state_version_after = table.Column<long>(type: "bigint", nullable: false),
                    transferred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_transfers", x => new { x.tenant_id, x.session_id, x.transfer_id });
                    table.CheckConstraint("ck_session_transfers_departments", "(from_department_id IS NULL OR from_business_unit_id IS NOT NULL) AND (to_department_id IS NULL OR to_business_unit_id IS NOT NULL)");
                    table.CheckConstraint("ck_session_transfers_state_version", "state_version_after >= 2");
                    table.ForeignKey(
                        name: "FK_session_transfers_service_sessions_tenant_id_session_id",
                        columns: x => new { x.tenant_id, x.session_id },
                        principalSchema: "conversations",
                        principalTable: "service_sessions",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_service_sessions_department",
                schema: "conversations",
                table: "service_sessions",
                sql: "department_id IS NULL OR business_unit_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_session_transfers_tenant_id_session_id_state_version_after",
                schema: "conversations",
                table: "session_transfers",
                columns: new[] { "tenant_id", "session_id", "state_version_after" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "session_transfers",
                schema: "conversations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_service_sessions_department",
                schema: "conversations",
                table: "service_sessions");

            migrationBuilder.DropColumn(
                name: "department_id",
                schema: "conversations",
                table: "service_sessions");

            migrationBuilder.RenameColumn(
                name: "channel_instance_id",
                schema: "conversations",
                table: "service_sessions",
                newName: "channel_id");

            migrationBuilder.RenameColumn(
                name: "channel_instance_id",
                schema: "conversations",
                table: "messages",
                newName: "channel_id");

            migrationBuilder.RenameIndex(
                name: "ix_messages_tenant_id_channel_instance_id_provider_event_id",
                schema: "conversations",
                table: "messages",
                newName: "ix_messages_tenant_id_channel_id_provider_event_id");

            migrationBuilder.RenameColumn(
                name: "channel_instance_id",
                schema: "conversations",
                table: "conversations",
                newName: "channel_id");

            migrationBuilder.RenameIndex(
                name: "ix_conversations_tenant_id_session_id_channel_instance_id",
                schema: "conversations",
                table: "conversations",
                newName: "ix_conversations_tenant_id_session_id_channel_id");

            migrationBuilder.RenameColumn(
                name: "channel_instance_id",
                schema: "conversations",
                table: "contacts",
                newName: "channel_id");

            migrationBuilder.RenameIndex(
                name: "ix_contacts_tenant_id_channel_instance_id_external_ref",
                schema: "conversations",
                table: "contacts",
                newName: "ix_contacts_tenant_id_channel_id_external_ref");
        }
    }
}
