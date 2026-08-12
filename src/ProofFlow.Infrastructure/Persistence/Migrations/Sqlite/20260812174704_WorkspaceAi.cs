using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProofFlow.Infrastructure.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class WorkspaceAi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AiBaseUrl",
                table: "Workspaces",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AiKeyCipher",
                table: "Workspaces",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AiKeyNonce",
                table: "Workspaces",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AiKeyPreview",
                table: "Workspaces",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AiKeyTag",
                table: "Workspaces",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AiKeyVersion",
                table: "Workspaces",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "AiModel",
                table: "Workspaces",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AiBaseUrl",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "AiKeyCipher",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "AiKeyNonce",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "AiKeyPreview",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "AiKeyTag",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "AiKeyVersion",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "AiModel",
                table: "Workspaces");
        }
    }
}
