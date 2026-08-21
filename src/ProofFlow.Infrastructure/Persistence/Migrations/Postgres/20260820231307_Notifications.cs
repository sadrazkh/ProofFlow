using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProofFlow.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class Notifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NotificationsSeenAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "NotifyByEmail",
                table: "Projects",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "WebhookAllowPrivate",
                table: "Projects",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "WebhookSecretCipher",
                table: "Projects",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WebhookSecretKeyVersion",
                table: "Projects",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "WebhookSecretNonce",
                table: "Projects",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WebhookSecretTag",
                table: "Projects",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WebhookUrl",
                table: "Projects",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    ArgsJson = table.Column<string>(type: "jsonb", maxLength: 2000, nullable: true),
                    LinkPath = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    TargetType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: true),
                    TargetLabel = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    EmailedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    WebhookAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    WebhookAttempts = table.Column<int>(type: "integer", nullable: false),
                    WebhookFailure = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Notifications_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_ProjectId_WebhookAt_EmailedAt",
                table: "Notifications",
                columns: new[] { "ProjectId", "WebhookAt", "EmailedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_WorkspaceId_CreatedAt",
                table: "Notifications",
                columns: new[] { "WorkspaceId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Notifications");

            migrationBuilder.DropColumn(
                name: "NotificationsSeenAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "NotifyByEmail",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "WebhookAllowPrivate",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "WebhookSecretCipher",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "WebhookSecretKeyVersion",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "WebhookSecretNonce",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "WebhookSecretTag",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "WebhookUrl",
                table: "Projects");
        }
    }
}
