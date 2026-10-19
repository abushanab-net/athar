using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Athar.Data.Migrations
{
    /// <inheritdoc />
    public partial class SpaceDeletedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deleted_at",
                table: "spaces",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "spaces");
        }
    }
}
