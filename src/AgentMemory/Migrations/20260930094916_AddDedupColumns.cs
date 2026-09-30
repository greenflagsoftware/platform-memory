using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentMemory.Migrations
{
    /// <inheritdoc />
    public partial class AddDedupColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_seen_at",
                table: "memories",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<int>(
                name: "seen_count",
                table: "memories",
                type: "integer",
                nullable: false,
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_seen_at",
                table: "memories");

            migrationBuilder.DropColumn(
                name: "seen_count",
                table: "memories");
        }
    }
}
