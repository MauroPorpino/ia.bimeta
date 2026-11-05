using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IaBimeta.Channels.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AccountSendState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "account_send_state",
                schema: "channels",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tokens = table.Column<double>(type: "double precision", nullable: false),
                    refilled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cooldown_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    is_paused = table.Column<bool>(type: "boolean", nullable: false),
                    pause_reason = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    paused_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    throughput_per_second = table.Column<int>(type: "integer", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_send_state", x => new { x.tenant_id, x.account_id });
                    table.CheckConstraint("ck_account_send_state_pause", "(is_paused AND pause_reason IS NOT NULL AND paused_at IS NOT NULL) OR (NOT is_paused AND pause_reason IS NULL)");
                    table.CheckConstraint("ck_account_send_state_throughput", "throughput_per_second IS NULL OR (throughput_per_second >= 1 AND throughput_per_second <= 1000)");
                    table.CheckConstraint("ck_account_send_state_tokens", "tokens >= 0");
                    table.ForeignKey(
                        name: "FK_account_send_state_channel_accounts_tenant_id_account_id",
                        columns: x => new { x.tenant_id, x.account_id },
                        principalSchema: "channels",
                        principalTable: "channel_accounts",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_send_state",
                schema: "channels");
        }
    }
}
