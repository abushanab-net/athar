using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Athar.Data.Migrations
{
    /// <inheritdoc />
    public partial class PhaseGAgentOnboarding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "commit_sha",
                table: "source_documents",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "fetched_at",
                table: "source_documents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "media_type",
                table: "source_documents",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "size_bytes",
                table: "source_documents",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "stored_file",
                table: "source_documents",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "revision",
                table: "skills",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<Guid>(
                name: "created_by",
                table: "knowledge_sources",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "title",
                table: "knowledge_sources",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "memory_id",
                table: "capture_candidates",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "proposal_id",
                table: "capture_candidates",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source",
                table: "agent_sessions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "hook");

            migrationBuilder.AddColumn<string>(
                name: "title",
                table: "agent_sessions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "capture_settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    repos = table.Column<string[]>(type: "text[]", nullable: false),
                    paused = table.Column<bool>(type: "boolean", nullable: false),
                    auto_accept_personal = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    auto_accept_shared = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_capture_settings", x => new { x.org_id, x.id });
                    table.ForeignKey(
                        name: "FK_capture_settings_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_capture_settings_users_org_id_user_id",
                        columns: x => new { x.org_id, x.user_id },
                        principalTable: "users",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "skill_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    skill_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    body_md = table.Column<string>(type: "text", nullable: false),
                    files = table.Column<string>(type: "jsonb", nullable: false),
                    published_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_skill_versions", x => new { x.org_id, x.id });
                    table.ForeignKey(
                        name: "FK_skill_versions_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_skill_versions_skills_org_id_skill_id",
                        columns: x => new { x.org_id, x.skill_id },
                        principalTable: "skills",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_skill_versions_users_org_id_published_by",
                        columns: x => new { x.org_id, x.published_by },
                        principalTable: "users",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_sources_org_id_created_by",
                table: "knowledge_sources",
                columns: new[] { "org_id", "created_by" });

            migrationBuilder.CreateIndex(
                name: "IX_capture_candidates_org_id_memory_id",
                table: "capture_candidates",
                columns: new[] { "org_id", "memory_id" });

            migrationBuilder.CreateIndex(
                name: "IX_capture_candidates_org_id_proposal_id",
                table: "capture_candidates",
                columns: new[] { "org_id", "proposal_id" });

            migrationBuilder.CreateIndex(
                name: "IX_capture_settings_org_id_user_id",
                table: "capture_settings",
                columns: new[] { "org_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_skill_versions_org_id_published_by",
                table: "skill_versions",
                columns: new[] { "org_id", "published_by" });

            migrationBuilder.CreateIndex(
                name: "IX_skill_versions_org_id_skill_id_revision",
                table: "skill_versions",
                columns: new[] { "org_id", "skill_id", "revision" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_capture_candidates_memories_org_id_memory_id",
                table: "capture_candidates",
                columns: new[] { "org_id", "memory_id" },
                principalTable: "memories",
                principalColumns: new[] { "org_id", "id" });

            migrationBuilder.AddForeignKey(
                name: "FK_capture_candidates_memory_proposals_org_id_proposal_id",
                table: "capture_candidates",
                columns: new[] { "org_id", "proposal_id" },
                principalTable: "memory_proposals",
                principalColumns: new[] { "org_id", "id" });

            migrationBuilder.AddForeignKey(
                name: "FK_knowledge_sources_users_org_id_created_by",
                table: "knowledge_sources",
                columns: new[] { "org_id", "created_by" },
                principalTable: "users",
                principalColumns: new[] { "org_id", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_capture_candidates_memories_org_id_memory_id",
                table: "capture_candidates");

            migrationBuilder.DropForeignKey(
                name: "FK_capture_candidates_memory_proposals_org_id_proposal_id",
                table: "capture_candidates");

            migrationBuilder.DropForeignKey(
                name: "FK_knowledge_sources_users_org_id_created_by",
                table: "knowledge_sources");

            migrationBuilder.DropTable(
                name: "capture_settings");

            migrationBuilder.DropTable(
                name: "skill_versions");

            migrationBuilder.DropIndex(
                name: "IX_knowledge_sources_org_id_created_by",
                table: "knowledge_sources");

            migrationBuilder.DropIndex(
                name: "IX_capture_candidates_org_id_memory_id",
                table: "capture_candidates");

            migrationBuilder.DropIndex(
                name: "IX_capture_candidates_org_id_proposal_id",
                table: "capture_candidates");

            migrationBuilder.DropColumn(
                name: "fetched_at",
                table: "source_documents");

            migrationBuilder.DropColumn(
                name: "media_type",
                table: "source_documents");

            migrationBuilder.DropColumn(
                name: "size_bytes",
                table: "source_documents");

            migrationBuilder.DropColumn(
                name: "stored_file",
                table: "source_documents");

            migrationBuilder.DropColumn(
                name: "revision",
                table: "skills");

            migrationBuilder.DropColumn(
                name: "created_by",
                table: "knowledge_sources");

            migrationBuilder.DropColumn(
                name: "title",
                table: "knowledge_sources");

            migrationBuilder.DropColumn(
                name: "memory_id",
                table: "capture_candidates");

            migrationBuilder.DropColumn(
                name: "proposal_id",
                table: "capture_candidates");

            migrationBuilder.DropColumn(
                name: "source",
                table: "agent_sessions");

            migrationBuilder.DropColumn(
                name: "title",
                table: "agent_sessions");

            migrationBuilder.AlterColumn<string>(
                name: "commit_sha",
                table: "source_documents",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }
    }
}
