using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Litos.SoftwareFactory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M2Concurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_task_runs_Status_CreatedAt",
                table: "task_runs");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "QueuedAt",
                table: "task_runs",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            // Runs from before this migration keep their old order: by when they were created.
            migrationBuilder.Sql("UPDATE task_runs SET \"QueuedAt\" = \"CreatedAt\";");

            migrationBuilder.AddColumn<string>(
                name: "WorkspaceSnapshotJson",
                table: "task_runs",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_task_runs_Status_QueuedAt",
                table: "task_runs",
                columns: new[] { "Status", "QueuedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_task_runs_Status_QueuedAt",
                table: "task_runs");

            migrationBuilder.DropColumn(
                name: "QueuedAt",
                table: "task_runs");

            migrationBuilder.DropColumn(
                name: "WorkspaceSnapshotJson",
                table: "task_runs");

            migrationBuilder.CreateIndex(
                name: "IX_task_runs_Status_CreatedAt",
                table: "task_runs",
                columns: new[] { "Status", "CreatedAt" });
        }
    }
}
