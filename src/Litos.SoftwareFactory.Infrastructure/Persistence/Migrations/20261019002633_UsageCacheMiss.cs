using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Litos.SoftwareFactory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UsageCacheMiss : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ActualCacheRead",
                table: "usage_entries",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "ExpectedCachedInput",
                table: "usage_entries",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActualCacheRead",
                table: "usage_entries");

            migrationBuilder.DropColumn(
                name: "ExpectedCachedInput",
                table: "usage_entries");
        }
    }
}
