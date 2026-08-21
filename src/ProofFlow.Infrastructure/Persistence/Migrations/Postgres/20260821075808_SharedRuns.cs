using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProofFlow.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class SharedRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ShareHash",
                table: "TestRuns",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SharedAt",
                table: "TestRuns",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TestRuns_ShareHash",
                table: "TestRuns",
                column: "ShareHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TestRuns_ShareHash",
                table: "TestRuns");

            migrationBuilder.DropColumn(
                name: "ShareHash",
                table: "TestRuns");

            migrationBuilder.DropColumn(
                name: "SharedAt",
                table: "TestRuns");
        }
    }
}
