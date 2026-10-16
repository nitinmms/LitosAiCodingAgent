using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Litos.SoftwareFactory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M4RunLessons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ManualLessonsJson",
                table: "task_threads",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.CreateTable(
                name: "run_lesson_snapshots",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    LessonId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<long>(type: "bigint", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    Rule = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Score = table.Column<int>(type: "integer", nullable: false),
                    Inclusion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Manual = table.Column<bool>(type: "boolean", nullable: false),
                    IncludedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Verdict = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    VerdictBy = table.Column<Guid>(type: "uuid", nullable: true),
                    VerdictAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_run_lesson_snapshots", x => new { x.RunId, x.LessonId });
                    table.ForeignKey(
                        name: "FK_run_lesson_snapshots_lessons_LessonId",
                        column: x => x.LessonId,
                        principalTable: "lessons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_run_lesson_snapshots_task_runs_RunId",
                        column: x => x.RunId,
                        principalTable: "task_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_run_lesson_snapshots_LessonId",
                table: "run_lesson_snapshots",
                column: "LessonId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "run_lesson_snapshots");

            migrationBuilder.DropColumn(
                name: "ManualLessonsJson",
                table: "task_threads");
        }
    }
}
