using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Litos.SoftwareFactory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M3ThreadPtc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PtcEnabled",
                table: "task_threads",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PtcEnabled",
                table: "task_threads");
        }
    }
}
