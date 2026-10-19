using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Athar.Data.Migrations
{
    /// <inheritdoc />
    public partial class PhaseUDraftsWatchesAvatars : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "avatar_file",
                table: "users",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "created_by",
                table: "pages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "published_at",
                table: "pages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "published_by",
                table: "pages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "pages",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "published");

            migrationBuilder.CreateTable(
                name: "page_watches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    page_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_page_watches", x => new { x.org_id, x.id });
                    table.ForeignKey(
                        name: "FK_page_watches_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_page_watches_pages_org_id_page_id",
                        columns: x => new { x.org_id, x.page_id },
                        principalTable: "pages",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_page_watches_users_org_id_user_id",
                        columns: x => new { x.org_id, x.user_id },
                        principalTable: "users",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_pages_org_id_created_by",
                table: "pages",
                columns: new[] { "org_id", "created_by" });

            migrationBuilder.CreateIndex(
                name: "IX_pages_org_id_published_by",
                table: "pages",
                columns: new[] { "org_id", "published_by" });

            migrationBuilder.CreateIndex(
                name: "IX_page_watches_org_id_page_id_user_id",
                table: "page_watches",
                columns: new[] { "org_id", "page_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_page_watches_org_id_user_id",
                table: "page_watches",
                columns: new[] { "org_id", "user_id" });

            migrationBuilder.AddForeignKey(
                name: "FK_pages_users_org_id_created_by",
                table: "pages",
                columns: new[] { "org_id", "created_by" },
                principalTable: "users",
                principalColumns: new[] { "org_id", "id" });

            migrationBuilder.AddForeignKey(
                name: "FK_pages_users_org_id_published_by",
                table: "pages",
                columns: new[] { "org_id", "published_by" },
                principalTable: "users",
                principalColumns: new[] { "org_id", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_pages_users_org_id_created_by",
                table: "pages");

            migrationBuilder.DropForeignKey(
                name: "FK_pages_users_org_id_published_by",
                table: "pages");

            migrationBuilder.DropTable(
                name: "page_watches");

            migrationBuilder.DropIndex(
                name: "IX_pages_org_id_created_by",
                table: "pages");

            migrationBuilder.DropIndex(
                name: "IX_pages_org_id_published_by",
                table: "pages");

            migrationBuilder.DropColumn(
                name: "avatar_file",
                table: "users");

            migrationBuilder.DropColumn(
                name: "created_by",
                table: "pages");

            migrationBuilder.DropColumn(
                name: "published_at",
                table: "pages");

            migrationBuilder.DropColumn(
                name: "published_by",
                table: "pages");

            migrationBuilder.DropColumn(
                name: "status",
                table: "pages");
        }
    }
}
