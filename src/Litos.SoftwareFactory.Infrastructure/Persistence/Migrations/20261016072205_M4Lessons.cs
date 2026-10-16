using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Litos.SoftwareFactory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M4Lessons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lessons",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CurrentRevision = table.Column<int>(type: "integer", nullable: false),
                    ApprovedRevision = table.Column<int>(type: "integer", nullable: true),
                    SupersededById = table.Column<Guid>(type: "uuid", nullable: true),
                    ProposedByJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lessons", x => x.Id);
                    table.ForeignKey(
                        name: "FK_lessons_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "lesson_revisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LessonId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    Rule = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: false),
                    TopicsJson = table.Column<string>(type: "jsonb", nullable: false),
                    PathsJson = table.Column<string>(type: "jsonb", nullable: false),
                    SourceKind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SourceRef = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceThreadId = table.Column<Guid>(type: "uuid", nullable: true),
                    Reason = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: false),
                    NormalizedHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AuthorId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ApprovedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lesson_revisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_lesson_revisions_lessons_LessonId",
                        column: x => x.LessonId,
                        principalTable: "lessons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_lesson_revisions_LessonId_Revision",
                table: "lesson_revisions",
                columns: new[] { "LessonId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_lesson_revisions_NormalizedHash",
                table: "lesson_revisions",
                column: "NormalizedHash");

            migrationBuilder.CreateIndex(
                name: "IX_lessons_ProjectId_Number",
                table: "lessons",
                columns: new[] { "ProjectId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_lessons_ProjectId_Status",
                table: "lessons",
                columns: new[] { "ProjectId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lesson_revisions");

            migrationBuilder.DropTable(
                name: "lessons");
        }
    }
}
