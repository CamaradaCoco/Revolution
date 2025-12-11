using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Demo.Migrations
{
    /// <inheritdoc />
    public partial class Addingstagingadminpageforcheckingresultsbeforedisplayingonmap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StagedRevolutions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    StartDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Country = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CountryIso = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CountryWikidataId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Latitude = table.Column<double>(type: "float", nullable: true),
                    Longitude = table.Column<double>(type: "float", nullable: true),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    WikidataId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Sources = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReviewedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Reviewer = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ReviewNotes = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StagedRevolutions", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StagedRevolutions");
        }
    }
}
