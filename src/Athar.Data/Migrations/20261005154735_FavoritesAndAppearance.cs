using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Athar.Data.Migrations
{
    /// <inheritdoc />
    public partial class FavoritesAndAppearance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "color",
                table: "workspaces",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "icon",
                table: "workspaces",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "color",
                table: "spaces",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "icon",
                table: "spaces",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "favorites",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: true),
                    space_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_favorites", x => new { x.org_id, x.id });
                    table.CheckConstraint("ck_favorites_one_target", "num_nonnulls(workspace_id, space_id) = 1");
                    table.ForeignKey(
                        name: "FK_favorites_organizations_org_id",
                        column: x => x.org_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_favorites_spaces_org_id_space_id",
                        columns: x => new { x.org_id, x.space_id },
                        principalTable: "spaces",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_favorites_users_org_id_user_id",
                        columns: x => new { x.org_id, x.user_id },
                        principalTable: "users",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_favorites_workspaces_org_id_workspace_id",
                        columns: x => new { x.org_id, x.workspace_id },
                        principalTable: "workspaces",
                        principalColumns: new[] { "org_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_favorites_org_id_space_id",
                table: "favorites",
                columns: new[] { "org_id", "space_id" });

            migrationBuilder.CreateIndex(
                name: "IX_favorites_org_id_user_id_space_id",
                table: "favorites",
                columns: new[] { "org_id", "user_id", "space_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_favorites_org_id_user_id_workspace_id",
                table: "favorites",
                columns: new[] { "org_id", "user_id", "workspace_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_favorites_org_id_workspace_id",
                table: "favorites",
                columns: new[] { "org_id", "workspace_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "favorites");

            migrationBuilder.DropColumn(
                name: "color",
                table: "workspaces");

            migrationBuilder.DropColumn(
                name: "icon",
                table: "workspaces");

            migrationBuilder.DropColumn(
                name: "color",
                table: "spaces");

            migrationBuilder.DropColumn(
                name: "icon",
                table: "spaces");
        }
    }
}
