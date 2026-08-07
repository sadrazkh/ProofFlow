using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProofFlow.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class RunBatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BatchId",
                table: "TestRuns",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RunBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Trigger = table.Column<int>(type: "integer", nullable: false),
                    StartedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Total = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunBatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RunBatches_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TestRuns_BatchId",
                table: "TestRuns",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_RunBatches_ProjectId_CreatedAt",
                table: "RunBatches",
                columns: new[] { "ProjectId", "CreatedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_TestRuns_RunBatches_BatchId",
                table: "TestRuns",
                column: "BatchId",
                principalTable: "RunBatches",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TestRuns_RunBatches_BatchId",
                table: "TestRuns");

            migrationBuilder.DropTable(
                name: "RunBatches");

            migrationBuilder.DropIndex(
                name: "IX_TestRuns_BatchId",
                table: "TestRuns");

            migrationBuilder.DropColumn(
                name: "BatchId",
                table: "TestRuns");
        }
    }
}
