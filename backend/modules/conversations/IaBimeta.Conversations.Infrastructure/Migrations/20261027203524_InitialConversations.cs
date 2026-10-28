using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.Conversations.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialConversations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "conversations");

            migrationBuilder.CreateTable(
                name: "contacts",
                schema: "conversations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_ref = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    display_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contacts", x => new { x.tenant_id, x.id });
                });

            migrationBuilder.CreateTable(
                name: "service_sessions",
                schema: "conversations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_unit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    contact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    handling_mode = table.Column<short>(type: "smallint", nullable: false),
                    responder_ref = table.Column<Guid>(type: "uuid", nullable: true),
                    state_version = table.Column<long>(type: "bigint", nullable: false),
                    last_message_seq = table.Column<long>(type: "bigint", nullable: false),
                    last_inbound_seq = table.Column<long>(type: "bigint", nullable: false),
                    answered_through_seq = table.Column<long>(type: "bigint", nullable: false),
                    active_turn_id = table.Column<Guid>(type: "uuid", nullable: true),
                    turn_epoch = table.Column<long>(type: "bigint", nullable: false),
                    last_lease_token = table.Column<long>(type: "bigint", nullable: false),
                    last_reply_accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reply_delivery_failed = table.Column<bool>(type: "boolean", nullable: false),
                    attention_raised_for_seq = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_message_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_sessions", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_service_sessions_responder", "handling_mode = 3 OR responder_ref IS NULL");
                    table.CheckConstraint("ck_service_sessions_sequences", "answered_through_seq >= 0 AND answered_through_seq <= last_inbound_seq AND last_inbound_seq <= last_message_seq");
                    table.CheckConstraint("ck_service_sessions_status", "status IN (1, 2) AND handling_mode IN (1, 2, 3)");
                    table.CheckConstraint("ck_service_sessions_versions", "state_version >= 1 AND turn_epoch >= 0 AND last_lease_token >= 0");
                    table.ForeignKey(
                        name: "FK_service_sessions_contacts_tenant_id_contact_id",
                        columns: x => new { x.tenant_id, x.contact_id },
                        principalSchema: "conversations",
                        principalTable: "contacts",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "conversations",
                schema: "conversations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversations", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "FK_conversations_contacts_tenant_id_contact_id",
                        columns: x => new { x.tenant_id, x.contact_id },
                        principalSchema: "conversations",
                        principalTable: "contacts",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_conversations_service_sessions_tenant_id_session_id",
                        columns: x => new { x.tenant_id, x.session_id },
                        principalSchema: "conversations",
                        principalTable: "service_sessions",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "handling_transitions",
                schema: "conversations",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state_version_after = table.Column<long>(type: "bigint", nullable: false),
                    applied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_handling_transitions", x => new { x.tenant_id, x.session_id, x.transition_id });
                    table.ForeignKey(
                        name: "FK_handling_transitions_service_sessions_tenant_id_session_id",
                        columns: x => new { x.tenant_id, x.session_id },
                        principalSchema: "conversations",
                        principalTable: "service_sessions",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "messages",
                schema: "conversations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    direction = table.Column<short>(type: "smallint", nullable: false),
                    message_seq = table.Column<long>(type: "bigint", nullable: false),
                    inbound_seq = table.Column<long>(type: "bigint", nullable: true),
                    author = table.Column<short>(type: "smallint", nullable: false),
                    author_ref = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    content_state = table.Column<short>(type: "smallint", nullable: false),
                    text = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    provider_event_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    provider_timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    late_arrival = table.Column<bool>(type: "boolean", nullable: false),
                    turn_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reply_kind = table.Column<short>(type: "smallint", nullable: true),
                    covered_through_seq = table.Column<long>(type: "bigint", nullable: true),
                    delivery_state = table.Column<short>(type: "smallint", nullable: true),
                    delivery_failure_reason = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    resend_of_message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resend_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: true),
                    ai_request_ids = table.Column<Guid[]>(type: "uuid[]", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_messages", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_messages_direction_shape", "(direction = 1 AND inbound_seq IS NOT NULL AND provider_event_id IS NOT NULL AND author = 1 AND delivery_state IS NULL) OR (direction = 2 AND inbound_seq IS NULL AND delivery_state IS NOT NULL AND author IN (2, 3))");
                    table.CheckConstraint("ck_messages_sequences", "message_seq >= 1 AND (inbound_seq IS NULL OR inbound_seq >= 1)");
                    table.ForeignKey(
                        name: "FK_messages_conversations_tenant_id_conversation_id",
                        columns: x => new { x.tenant_id, x.conversation_id },
                        principalSchema: "conversations",
                        principalTable: "conversations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_messages_service_sessions_tenant_id_session_id",
                        columns: x => new { x.tenant_id, x.session_id },
                        principalSchema: "conversations",
                        principalTable: "service_sessions",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_contacts_tenant_id_channel_id_external_ref",
                schema: "conversations",
                table: "contacts",
                columns: new[] { "tenant_id", "channel_id", "external_ref" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_conversations_tenant_id_contact_id",
                schema: "conversations",
                table: "conversations",
                columns: new[] { "tenant_id", "contact_id" });

            migrationBuilder.CreateIndex(
                name: "ix_conversations_tenant_id_session_id_channel_id",
                schema: "conversations",
                table: "conversations",
                columns: new[] { "tenant_id", "session_id", "channel_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_messages_history",
                schema: "conversations",
                table: "messages",
                columns: new[] { "tenant_id", "session_id", "occurred_at", "message_seq" });

            migrationBuilder.CreateIndex(
                name: "ix_messages_one_reply_per_turn",
                schema: "conversations",
                table: "messages",
                columns: new[] { "tenant_id", "session_id", "turn_id" },
                unique: true,
                filter: "turn_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_messages_tenant_id_channel_id_provider_event_id",
                schema: "conversations",
                table: "messages",
                columns: new[] { "tenant_id", "channel_id", "provider_event_id" },
                unique: true,
                filter: "provider_event_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_messages_tenant_id_conversation_id",
                schema: "conversations",
                table: "messages",
                columns: new[] { "tenant_id", "conversation_id" });

            migrationBuilder.CreateIndex(
                name: "ix_messages_tenant_id_resend_of_message_id",
                schema: "conversations",
                table: "messages",
                columns: new[] { "tenant_id", "resend_of_message_id" },
                filter: "resend_of_message_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_messages_tenant_id_resend_request_id",
                schema: "conversations",
                table: "messages",
                columns: new[] { "tenant_id", "resend_request_id" },
                unique: true,
                filter: "resend_request_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_messages_tenant_id_session_id_inbound_seq",
                schema: "conversations",
                table: "messages",
                columns: new[] { "tenant_id", "session_id", "inbound_seq" },
                unique: true,
                filter: "inbound_seq IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_messages_tenant_id_session_id_message_seq",
                schema: "conversations",
                table: "messages",
                columns: new[] { "tenant_id", "session_id", "message_seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_service_sessions_one_open_per_contact",
                schema: "conversations",
                table: "service_sessions",
                columns: new[] { "tenant_id", "contact_id" },
                unique: true,
                filter: "status = 1");

            migrationBuilder.CreateIndex(
                name: "ix_service_sessions_tenant_id_organization_id_last_message_at",
                schema: "conversations",
                table: "service_sessions",
                columns: new[] { "tenant_id", "organization_id", "last_message_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "handling_transitions",
                schema: "conversations");

            migrationBuilder.DropTable(
                name: "messages",
                schema: "conversations");

            migrationBuilder.DropTable(
                name: "conversations",
                schema: "conversations");

            migrationBuilder.DropTable(
                name: "service_sessions",
                schema: "conversations");

            migrationBuilder.DropTable(
                name: "contacts",
                schema: "conversations");
        }
    }
}
