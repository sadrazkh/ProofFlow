using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProofFlow.Infrastructure.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class ScheduledChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LastCheckBatchId",
                table: "RunSchedules",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "DataSetVersionId",
                table: "CaptureSessions",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AddColumn<Guid>(
                name: "BatchId",
                table: "CaptureSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CheckBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Trigger = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    FinishedAt = table.Column<string>(type: "TEXT", nullable: true),
                    Total = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CheckBatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CheckBatches_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScheduleBaselines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunScheduleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BaselineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduleBaselines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScheduleBaselines_Baselines_BaselineId",
                        column: x => x.BaselineId,
                        principalTable: "Baselines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ScheduleBaselines_RunSchedules_RunScheduleId",
                        column: x => x.RunScheduleId,
                        principalTable: "RunSchedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CaptureSessions_BatchId",
                table: "CaptureSessions",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_CheckBatches_ProjectId_CreatedAt",
                table: "CheckBatches",
                columns: new[] { "ProjectId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleBaselines_BaselineId",
                table: "ScheduleBaselines",
                column: "BaselineId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleBaselines_RunScheduleId_BaselineId",
                table: "ScheduleBaselines",
                columns: new[] { "RunScheduleId", "BaselineId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_CaptureSessions_CheckBatches_BatchId",
                table: "CaptureSessions",
                column: "BatchId",
                principalTable: "CheckBatches",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CaptureSessions_CheckBatches_BatchId",
                table: "CaptureSessions");

            migrationBuilder.DropTable(
                name: "CheckBatches");

            migrationBuilder.DropTable(
                name: "ScheduleBaselines");

            migrationBuilder.DropIndex(
                name: "IX_CaptureSessions_BatchId",
                table: "CaptureSessions");

            migrationBuilder.DropColumn(
                name: "LastCheckBatchId",
                table: "RunSchedules");

            migrationBuilder.DropColumn(
                name: "BatchId",
                table: "CaptureSessions");

            migrationBuilder.AlterColumn<Guid>(
                name: "DataSetVersionId",
                table: "CaptureSessions",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);
        }
    }
}
