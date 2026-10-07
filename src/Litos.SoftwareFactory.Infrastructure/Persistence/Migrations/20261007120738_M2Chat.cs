using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Litos.SoftwareFactory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M2Chat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_task_runs_one_active_per_thread",
                table: "task_runs");

            migrationBuilder.AddColumn<long>(
                name: "ChatBudgetCap",
                table: "task_runs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ChatTokensReserved",
                table: "task_runs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "ChatTokensUsed",
                table: "task_runs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "ix_task_runs_one_active_per_thread",
                table: "task_runs",
                column: "ThreadId",
                unique: true,
                filter: "\"Status\" <> 'Finished' AND \"Kind\" <> 'Chat'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_task_runs_one_active_per_thread",
                table: "task_runs");

            migrationBuilder.DropColumn(
                name: "ChatBudgetCap",
                table: "task_runs");

            migrationBuilder.DropColumn(
                name: "ChatTokensReserved",
                table: "task_runs");

            migrationBuilder.DropColumn(
                name: "ChatTokensUsed",
                table: "task_runs");

            migrationBuilder.CreateIndex(
                name: "ix_task_runs_one_active_per_thread",
                table: "task_runs",
                column: "ThreadId",
                unique: true,
                filter: "\"Status\" <> 'Finished'");
        }
    }
}
