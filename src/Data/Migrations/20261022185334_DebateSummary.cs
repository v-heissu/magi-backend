using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Magi.Api.src.Data.Migrations
{
    /// <inheritdoc />
    public partial class DebateSummary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Summary",
                table: "Deliberations",
                type: "text",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "SummaryError",
                table: "Deliberations",
                type: "varchar(500)",
                maxLength: 500,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "SummaryModel",
                table: "Deliberations",
                type: "varchar(100)",
                maxLength: 100,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Summary",
                table: "Deliberations");

            migrationBuilder.DropColumn(
                name: "SummaryError",
                table: "Deliberations");

            migrationBuilder.DropColumn(
                name: "SummaryModel",
                table: "Deliberations");
        }
    }
}
