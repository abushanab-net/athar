using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Athar.Data.Migrations
{
    /// <inheritdoc />
    public partial class AgentUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "daily_agent_stats",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    client = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    count = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_daily_agent_stats", x => new { x.org_id, x.id });
                    table.ForeignKey(
                        name: "FK_daily_agent_stats_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mcp_calls",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    credential_id = table.Column<Guid>(type: "uuid", nullable: true),
                    oauth_client_id = table.Column<Guid>(type: "uuid", nullable: true),
                    claimed_client = table.Column<string>(type: "text", nullable: true),
                    ip = table.Column<string>(type: "text", nullable: true),
                    user_agent = table.Column<string>(type: "text", nullable: true),
                    tool = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    duration_ms = table.Column<int>(type: "integer", nullable: false),
                    outcome = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    result_count = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mcp_calls", x => new { x.org_id, x.id });
                    table.ForeignKey(
                        name: "FK_mcp_calls_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "model_usage",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    model_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    input_tokens = table.Column<int>(type: "integer", nullable: false),
                    output_tokens = table.Column<int>(type: "integer", nullable: false),
                    duration_ms = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_usage", x => new { x.org_id, x.id });
                    table.ForeignKey(
                        name: "FK_model_usage_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_daily_agent_stats_org_id_date_user_id_client_kind",
                table: "daily_agent_stats",
                columns: new[] { "org_id", "date", "user_id", "client", "kind" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_mcp_calls_org_id_created_at",
                table: "mcp_calls",
                columns: new[] { "org_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_model_usage_org_id_created_at",
                table: "model_usage",
                columns: new[] { "org_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "daily_agent_stats");

            migrationBuilder.DropTable(
                name: "mcp_calls");

            migrationBuilder.DropTable(
                name: "model_usage");
        }
    }
}
