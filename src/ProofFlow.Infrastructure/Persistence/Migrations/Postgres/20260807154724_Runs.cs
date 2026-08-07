using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProofFlow.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class Runs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RunArtifacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    TestRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SizeBytes = table.Column<int>(type: "integer", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunArtifacts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TestRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScenarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScenarioVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    DataSetVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    DefinitionJson = table.Column<string>(type: "jsonb", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Trigger = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DurationMs = table.Column<double>(type: "double precision", nullable: false),
                    StepsRun = table.Column<int>(type: "integer", nullable: false),
                    StepsFailed = table.Column<int>(type: "integer", nullable: false),
                    AssertionsPassed = table.Column<int>(type: "integer", nullable: false),
                    AssertionsFailed = table.Column<int>(type: "integer", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    StartedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CancelledByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TestRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TestRuns_DataSetVersions_DataSetVersionId",
                        column: x => x.DataSetVersionId,
                        principalTable: "DataSetVersions",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_TestRuns_Environments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "Environments",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_TestRuns_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TestRuns_TestScenarios_ScenarioId",
                        column: x => x.ScenarioId,
                        principalTable: "TestScenarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "NodeRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    TestRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    NodeKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    NodeName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Iteration = table.Column<int>(type: "integer", nullable: false),
                    Attempt = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DurationMs = table.Column<double>(type: "double precision", nullable: false),
                    TakenPort = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    OutputJson = table.Column<string>(type: "jsonb", nullable: true),
                    FailureMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NodeRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NodeRuns_TestRuns_TestRunId",
                        column: x => x.TestRunId,
                        principalTable: "TestRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RunEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    TestRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    Level = table.Column<int>(type: "integer", nullable: false),
                    Message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    NodeId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    NodeName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DataJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RunEvents_TestRuns_TestRunId",
                        column: x => x.TestRunId,
                        principalTable: "TestRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AssertionResults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Passed = table.Column<bool>(type: "boolean", nullable: false),
                    Soft = table.Column<bool>(type: "boolean", nullable: false),
                    Expected = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Actual = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Target = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssertionResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssertionResults_NodeRuns_NodeRunId",
                        column: x => x.NodeRunId,
                        principalTable: "NodeRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssertionResults_NodeRunId_Passed",
                table: "AssertionResults",
                columns: new[] { "NodeRunId", "Passed" });

            migrationBuilder.CreateIndex(
                name: "IX_NodeRuns_TestRunId_NodeId",
                table: "NodeRuns",
                columns: new[] { "TestRunId", "NodeId" });

            migrationBuilder.CreateIndex(
                name: "IX_NodeRuns_TestRunId_SortOrder",
                table: "NodeRuns",
                columns: new[] { "TestRunId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_RunArtifacts_TestRunId_NodeRunId",
                table: "RunArtifacts",
                columns: new[] { "TestRunId", "NodeRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_RunEvents_TestRunId_Level",
                table: "RunEvents",
                columns: new[] { "TestRunId", "Level" });

            migrationBuilder.CreateIndex(
                name: "IX_RunEvents_TestRunId_Sequence",
                table: "RunEvents",
                columns: new[] { "TestRunId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TestRuns_DataSetVersionId",
                table: "TestRuns",
                column: "DataSetVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_TestRuns_EnvironmentId",
                table: "TestRuns",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_TestRuns_ProjectId_CreatedAt",
                table: "TestRuns",
                columns: new[] { "ProjectId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TestRuns_ScenarioId_CreatedAt",
                table: "TestRuns",
                columns: new[] { "ScenarioId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TestRuns_WorkspaceId_Status",
                table: "TestRuns",
                columns: new[] { "WorkspaceId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssertionResults");

            migrationBuilder.DropTable(
                name: "RunArtifacts");

            migrationBuilder.DropTable(
                name: "RunEvents");

            migrationBuilder.DropTable(
                name: "NodeRuns");

            migrationBuilder.DropTable(
                name: "TestRuns");
        }
    }
}
