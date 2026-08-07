using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProofFlow.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class DataSetsAndCapture : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BaselineSamples",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    BaselineId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    ContentType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    StatusCode = table.Column<int>(type: "integer", nullable: false),
                    NormalizedHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DataSetVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedFromSampleId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BaselineSamples", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BaselineSamples_Baselines_BaselineId",
                        column: x => x.BaselineId,
                        principalTable: "Baselines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DataSets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    KeyColumn = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CurrentVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataSets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DataSets_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DataSetVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    DataSetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    ColumnsJson = table.Column<string>(type: "jsonb", nullable: true),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    RowCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataSetVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DataSetVersions_DataSets_DataSetId",
                        column: x => x.DataSetId,
                        principalTable: "DataSets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CaptureSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    BaselineId = table.Column<Guid>(type: "uuid", nullable: false),
                    DataSetVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Mode = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    TotalRows = table.Column<int>(type: "integer", nullable: false),
                    Completed = table.Column<int>(type: "integer", nullable: false),
                    Differing = table.Column<int>(type: "integer", nullable: false),
                    Failed = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StoppedReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    StartedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaptureSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CaptureSessions_Baselines_BaselineId",
                        column: x => x.BaselineId,
                        principalTable: "Baselines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CaptureSessions_DataSetVersions_DataSetVersionId",
                        column: x => x.DataSetVersionId,
                        principalTable: "DataSetVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CaptureSessions_Environments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "Environments",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CaptureSessions_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DataSetRows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    DataSetVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    Key = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    ValuesJson = table.Column<string>(type: "jsonb", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataSetRows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DataSetRows_DataSetVersions_DataSetVersionId",
                        column: x => x.DataSetVersionId,
                        principalTable: "DataSetVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CaptureSamples",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    CaptureSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ResolvedUrl = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    StatusCode = table.Column<int>(type: "integer", nullable: false),
                    ContentType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Body = table.Column<string>(type: "text", nullable: true),
                    NormalizedHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DurationMs = table.Column<double>(type: "double precision", nullable: false),
                    Differs = table.Column<bool>(type: "boolean", nullable: false),
                    DiffSummaryJson = table.Column<string>(type: "jsonb", nullable: true),
                    FailureMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ReviewedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReviewNote = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaptureSamples", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CaptureSamples_CaptureSessions_CaptureSessionId",
                        column: x => x.CaptureSessionId,
                        principalTable: "CaptureSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BaselineSamples_BaselineId_Key",
                table: "BaselineSamples",
                columns: new[] { "BaselineId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CaptureSamples_CaptureSessionId_Key",
                table: "CaptureSamples",
                columns: new[] { "CaptureSessionId", "Key" });

            migrationBuilder.CreateIndex(
                name: "IX_CaptureSamples_CaptureSessionId_Ordinal",
                table: "CaptureSamples",
                columns: new[] { "CaptureSessionId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CaptureSamples_CaptureSessionId_Status_Differs",
                table: "CaptureSamples",
                columns: new[] { "CaptureSessionId", "Status", "Differs" });

            migrationBuilder.CreateIndex(
                name: "IX_CaptureSessions_BaselineId_Status",
                table: "CaptureSessions",
                columns: new[] { "BaselineId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_CaptureSessions_DataSetVersionId",
                table: "CaptureSessions",
                column: "DataSetVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_CaptureSessions_EnvironmentId",
                table: "CaptureSessions",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_CaptureSessions_ProjectId_StartedAt",
                table: "CaptureSessions",
                columns: new[] { "ProjectId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DataSetRows_DataSetVersionId_Key",
                table: "DataSetRows",
                columns: new[] { "DataSetVersionId", "Key" });

            migrationBuilder.CreateIndex(
                name: "IX_DataSetRows_DataSetVersionId_Ordinal",
                table: "DataSetRows",
                columns: new[] { "DataSetVersionId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DataSets_ProjectId",
                table: "DataSets",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_DataSets_WorkspaceId_ProjectId_Name",
                table: "DataSets",
                columns: new[] { "WorkspaceId", "ProjectId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DataSetVersions_DataSetId_Number",
                table: "DataSetVersions",
                columns: new[] { "DataSetId", "Number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BaselineSamples");

            migrationBuilder.DropTable(
                name: "CaptureSamples");

            migrationBuilder.DropTable(
                name: "DataSetRows");

            migrationBuilder.DropTable(
                name: "CaptureSessions");

            migrationBuilder.DropTable(
                name: "DataSetVersions");

            migrationBuilder.DropTable(
                name: "DataSets");
        }
    }
}
