using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Demo.Migrations
{
    public partial class FixCountryWikidataId : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add CountryWikidataId only if it does not already exist (safe to run against DBs with different states)
            migrationBuilder.Sql(@"
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE Name = N'CountryWikidataId' AND Object_ID = Object_ID(N'dbo.Revolutions')
)
BEGIN
    ALTER TABLE dbo.Revolutions ADD CountryWikidataId nvarchar(max) NULL;
END
");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Remove the column only if it exists (reversible)
            migrationBuilder.Sql(@"
IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE Name = N'CountryWikidataId' AND Object_ID = Object_ID(N'dbo.Revolutions')
)
BEGIN
    ALTER TABLE dbo.Revolutions DROP COLUMN CountryWikidataId;
END
");
        }
    }
}