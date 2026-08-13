using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProofFlow.Infrastructure.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class EndpointInputs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DataSetId",
                table: "Baselines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Baselines_DataSetId",
                table: "Baselines",
                column: "DataSetId");

            migrationBuilder.AddForeignKey(
                name: "FK_Baselines_DataSets_DataSetId",
                table: "Baselines",
                column: "DataSetId",
                principalTable: "DataSets",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Baselines_DataSets_DataSetId",
                table: "Baselines");

            migrationBuilder.DropIndex(
                name: "IX_Baselines_DataSetId",
                table: "Baselines");

            migrationBuilder.DropColumn(
                name: "DataSetId",
                table: "Baselines");
        }
    }
}
