using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Athar.Data.Migrations
{
    /// <inheritdoc />
    public partial class AdminAiSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    uses_environment = table.Column<bool>(type: "boolean", nullable: false),
                    embedding_generation = table.Column<long>(type: "bigint", nullable: false),
                    base_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    embedding_model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    query_model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    vision_model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    protected_api_key = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_settings", x => new { x.org_id, x.id });
                    table.ForeignKey(
                        name: "FK_ai_settings_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_settings_org_id",
                table: "ai_settings",
                column: "org_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_settings");
        }
    }
}
