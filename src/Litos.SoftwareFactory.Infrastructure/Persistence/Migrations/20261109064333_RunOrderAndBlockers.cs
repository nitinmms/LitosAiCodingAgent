using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Litos.SoftwareFactory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RunOrderAndBlockers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "BlockerNeedsYou",
                table: "task_threads",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PullRequestClosedAt",
                table: "task_threads",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PullRequestMergedAt",
                table: "task_threads",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "QueueRank",
                table: "task_threads",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RunAfterThreadId",
                table: "task_threads",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RunFirst",
                table: "task_threads",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "task_blockers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ThreadId = table.Column<Guid>(type: "uuid", nullable: false),
                    BlockerThreadId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReleasedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    ReleaseReason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    RemindedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_blockers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_task_blockers_task_threads_BlockerThreadId",
                        column: x => x.BlockerThreadId,
                        principalTable: "task_threads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_task_blockers_task_threads_ThreadId",
                        column: x => x.ThreadId,
                        principalTable: "task_threads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_task_blockers_BlockerThreadId",
                table: "task_blockers",
                column: "BlockerThreadId");

            migrationBuilder.CreateIndex(
                name: "ix_task_blockers_one_open_per_pair",
                table: "task_blockers",
                columns: new[] { "ThreadId", "BlockerThreadId" },
                unique: true,
                filter: "\"ReleasedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_task_blockers_ProjectId",
                table: "task_blockers",
                column: "ProjectId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "task_blockers");

            migrationBuilder.DropColumn(
                name: "BlockerNeedsYou",
                table: "task_threads");

            migrationBuilder.DropColumn(
                name: "PullRequestClosedAt",
                table: "task_threads");

            migrationBuilder.DropColumn(
                name: "PullRequestMergedAt",
                table: "task_threads");

            migrationBuilder.DropColumn(
                name: "QueueRank",
                table: "task_threads");

            migrationBuilder.DropColumn(
                name: "RunAfterThreadId",
                table: "task_threads");

            migrationBuilder.DropColumn(
                name: "RunFirst",
                table: "task_threads");
        }
    }
}
