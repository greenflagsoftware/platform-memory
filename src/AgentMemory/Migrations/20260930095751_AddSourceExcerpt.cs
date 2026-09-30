using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentMemory.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceExcerpt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "source_excerpt",
                table: "memories",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "source_excerpt",
                table: "memories");
        }
    }
}
