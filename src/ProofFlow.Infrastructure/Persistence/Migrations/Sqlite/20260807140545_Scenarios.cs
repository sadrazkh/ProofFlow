using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProofFlow.Infrastructure.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class Scenarios : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TestSuites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ArchivedAt = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TestSuites", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TestSuites_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TestScenarios",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TestSuiteId = table.Column<Guid>(type: "TEXT", nullable: true),
                    EnvironmentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    PublishedVersionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DraftVersionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ArchivedAt = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TestScenarios", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TestScenarios_Environments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "Environments",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_TestScenarios_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TestScenarios_TestSuites_TestSuiteId",
                        column: x => x.TestSuiteId,
                        principalTable: "TestSuites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ScenarioVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ScenarioId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CanvasJson = table.Column<string>(type: "TEXT", nullable: true),
                    ValidationJson = table.Column<string>(type: "TEXT", nullable: true),
                    IsValid = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PublishedAt = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScenarioVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScenarioVersions_TestScenarios_ScenarioId",
                        column: x => x.ScenarioId,
                        principalTable: "TestScenarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowNodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ScenarioVersionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    X = table.Column<double>(type: "REAL", nullable: false),
                    Y = table.Column<double>(type: "REAL", nullable: false),
                    ParentNodeId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PropertiesJson = table.Column<string>(type: "TEXT", nullable: true),
                    Disabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowNodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkflowNodes_ScenarioVersions_ScenarioVersionId",
                        column: x => x.ScenarioVersionId,
                        principalTable: "ScenarioVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkflowNodes_WorkflowNodes_ParentNodeId",
                        column: x => x.ParentNodeId,
                        principalTable: "WorkflowNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ScenarioVersionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FromNodeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FromPort = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    ToNodeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ToPort = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowConnections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkflowConnections_ScenarioVersions_ScenarioVersionId",
                        column: x => x.ScenarioVersionId,
                        principalTable: "ScenarioVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkflowConnections_WorkflowNodes_FromNodeId",
                        column: x => x.FromNodeId,
                        principalTable: "WorkflowNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkflowConnections_WorkflowNodes_ToNodeId",
                        column: x => x.ToNodeId,
                        principalTable: "WorkflowNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScenarioVersions_ScenarioId_Number",
                table: "ScenarioVersions",
                columns: new[] { "ScenarioId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScenarioVersions_ScenarioId_Status",
                table: "ScenarioVersions",
                columns: new[] { "ScenarioId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_TestScenarios_EnvironmentId",
                table: "TestScenarios",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_TestScenarios_ProjectId_TestSuiteId",
                table: "TestScenarios",
                columns: new[] { "ProjectId", "TestSuiteId" });

            migrationBuilder.CreateIndex(
                name: "IX_TestScenarios_TestSuiteId",
                table: "TestScenarios",
                column: "TestSuiteId");

            migrationBuilder.CreateIndex(
                name: "IX_TestScenarios_WorkspaceId_ProjectId_Name",
                table: "TestScenarios",
                columns: new[] { "WorkspaceId", "ProjectId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TestSuites_ProjectId",
                table: "TestSuites",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_TestSuites_WorkspaceId_ProjectId_Name",
                table: "TestSuites",
                columns: new[] { "WorkspaceId", "ProjectId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowConnections_FromNodeId",
                table: "WorkflowConnections",
                column: "FromNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowConnections_ScenarioVersionId_FromNodeId_FromPort_ToNodeId_ToPort",
                table: "WorkflowConnections",
                columns: new[] { "ScenarioVersionId", "FromNodeId", "FromPort", "ToNodeId", "ToPort" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowConnections_ScenarioVersionId_ToNodeId",
                table: "WorkflowConnections",
                columns: new[] { "ScenarioVersionId", "ToNodeId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowConnections_ToNodeId",
                table: "WorkflowConnections",
                column: "ToNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowNodes_ParentNodeId",
                table: "WorkflowNodes",
                column: "ParentNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowNodes_ScenarioVersionId_Name",
                table: "WorkflowNodes",
                columns: new[] { "ScenarioVersionId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowNodes_ScenarioVersionId_ParentNodeId",
                table: "WorkflowNodes",
                columns: new[] { "ScenarioVersionId", "ParentNodeId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkflowConnections");

            migrationBuilder.DropTable(
                name: "WorkflowNodes");

            migrationBuilder.DropTable(
                name: "ScenarioVersions");

            migrationBuilder.DropTable(
                name: "TestScenarios");

            migrationBuilder.DropTable(
                name: "TestSuites");
        }
    }
}
