using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProofFlow.Infrastructure.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class Scheduling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "QuarantineReason",
                table: "TestScenarios",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QuarantinedAt",
                table: "TestScenarios",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "QuarantinedByUserId",
                table: "TestScenarios",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ApiKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Preview = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ExpiresAt = table.Column<string>(type: "TEXT", nullable: true),
                    RevokedAt = table.Column<string>(type: "TEXT", nullable: true),
                    LastUsedAt = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApiKeys_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "RunSchedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Cron = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    TimeZoneId = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    NextRunAt = table.Column<string>(type: "TEXT", nullable: true),
                    LastRunAt = table.Column<string>(type: "TEXT", nullable: true),
                    LastBatchId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Problem = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunSchedules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RunSchedules_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScheduleEnvironments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunScheduleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduleEnvironments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScheduleEnvironments_Environments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "Environments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ScheduleEnvironments_RunSchedules_RunScheduleId",
                        column: x => x.RunScheduleId,
                        principalTable: "RunSchedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScheduleScenarios",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunScheduleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ScenarioId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduleScenarios", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScheduleScenarios_RunSchedules_RunScheduleId",
                        column: x => x.RunScheduleId,
                        principalTable: "RunSchedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ScheduleScenarios_TestScenarios_ScenarioId",
                        column: x => x.ScenarioId,
                        principalTable: "TestScenarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_Hash",
                table: "ApiKeys",
                column: "Hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_ProjectId",
                table: "ApiKeys",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_WorkspaceId_RevokedAt",
                table: "ApiKeys",
                columns: new[] { "WorkspaceId", "RevokedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RunSchedules_Enabled_NextRunAt",
                table: "RunSchedules",
                columns: new[] { "Enabled", "NextRunAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RunSchedules_ProjectId_Name",
                table: "RunSchedules",
                columns: new[] { "ProjectId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleEnvironments_EnvironmentId",
                table: "ScheduleEnvironments",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleEnvironments_RunScheduleId_EnvironmentId",
                table: "ScheduleEnvironments",
                columns: new[] { "RunScheduleId", "EnvironmentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleScenarios_RunScheduleId_ScenarioId",
                table: "ScheduleScenarios",
                columns: new[] { "RunScheduleId", "ScenarioId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleScenarios_ScenarioId",
                table: "ScheduleScenarios",
                column: "ScenarioId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiKeys");

            migrationBuilder.DropTable(
                name: "ScheduleEnvironments");

            migrationBuilder.DropTable(
                name: "ScheduleScenarios");

            migrationBuilder.DropTable(
                name: "RunSchedules");

            migrationBuilder.DropColumn(
                name: "QuarantineReason",
                table: "TestScenarios");

            migrationBuilder.DropColumn(
                name: "QuarantinedAt",
                table: "TestScenarios");

            migrationBuilder.DropColumn(
                name: "QuarantinedByUserId",
                table: "TestScenarios");
        }
    }
}
