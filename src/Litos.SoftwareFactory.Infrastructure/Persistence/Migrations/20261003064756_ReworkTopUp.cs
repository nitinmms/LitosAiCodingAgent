using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Litos.SoftwareFactory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReworkTopUp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "InitialBudgetCap",
                table: "task_threads",
                type: "bigint",
                nullable: true);

            // A thread created before this has no record of its first cap; its current one is the
            // best there is.
            migrationBuilder.Sql("UPDATE task_threads SET \"InitialBudgetCap\" = \"BudgetCap\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InitialBudgetCap",
                table: "task_threads");
        }
    }
}
