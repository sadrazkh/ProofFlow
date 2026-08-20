using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProofFlow.Infrastructure.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class ProjectBadge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BadgeHash",
                table: "Projects",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BadgePreview",
                table: "Projects",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Projects_BadgeHash",
                table: "Projects",
                column: "BadgeHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Projects_BadgeHash",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "BadgeHash",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "BadgePreview",
                table: "Projects");
        }
    }
}
