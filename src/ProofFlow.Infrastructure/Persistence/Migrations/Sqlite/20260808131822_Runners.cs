using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProofFlow.Infrastructure.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class Runners : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Runners",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    EnrollmentHash = table.Column<string>(type: "TEXT", nullable: true),
                    EnrollmentExpiresAt = table.Column<string>(type: "TEXT", nullable: true),
                    EnrolledAt = table.Column<string>(type: "TEXT", nullable: true),
                    TokenHash = table.Column<string>(type: "TEXT", nullable: true),
                    TokenPreview = table.Column<string>(type: "TEXT", nullable: false),
                    SigningKeyCipher = table.Column<string>(type: "TEXT", nullable: true),
                    SigningKeyNonce = table.Column<string>(type: "TEXT", nullable: true),
                    SigningKeyTag = table.Column<string>(type: "TEXT", nullable: true),
                    SigningKeyVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    Hostname = table.Column<string>(type: "TEXT", nullable: true),
                    Version = table.Column<string>(type: "TEXT", nullable: true),
                    LastSeenAt = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RevokedAt = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Runners", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Runners");
        }
    }
}
