using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProofFlow.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class ScenarioInputs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InputsJson",
                table: "TestScenarios",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InputsJson",
                table: "TestRuns",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InputsJson",
                table: "TestScenarios");

            migrationBuilder.DropColumn(
                name: "InputsJson",
                table: "TestRuns");
        }
    }
}
