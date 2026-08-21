using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProofFlow.Infrastructure.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class EndpointContract : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContractJson",
                table: "Baselines",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContractJson",
                table: "Baselines");
        }
    }
}
