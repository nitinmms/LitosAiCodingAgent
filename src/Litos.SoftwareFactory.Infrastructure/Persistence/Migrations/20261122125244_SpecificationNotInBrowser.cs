using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Litos.SoftwareFactory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SpecificationNotInBrowser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NotInBrowserJson",
                table: "specifications",
                type: "jsonb",
                nullable: false,
                // Existing specifications name no such criteria: an empty array, which jsonb accepts ("" it would not).
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NotInBrowserJson",
                table: "specifications");
        }
    }
}
