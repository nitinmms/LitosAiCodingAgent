using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Litos.SoftwareFactory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M2Spec : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SpecificationRevision",
                table: "task_runs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AffectedAreasJson",
                table: "specifications",
                type: "jsonb",
                nullable: false,
                // Existing revisions had none: an empty list, which jsonb accepts where "" is not JSON.
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "OpenQuestionsJson",
                table: "specifications",
                type: "jsonb",
                nullable: false,
                // Existing revisions had none: an empty list, which jsonb accepts where "" is not JSON.
                defaultValue: "[]");

            migrationBuilder.AddColumn<Guid>(
                name: "RunId",
                table: "specifications",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TestPlan",
                table: "specifications",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SpecificationRevision",
                table: "task_runs");

            migrationBuilder.DropColumn(
                name: "AffectedAreasJson",
                table: "specifications");

            migrationBuilder.DropColumn(
                name: "OpenQuestionsJson",
                table: "specifications");

            migrationBuilder.DropColumn(
                name: "RunId",
                table: "specifications");

            migrationBuilder.DropColumn(
                name: "TestPlan",
                table: "specifications");
        }
    }
}
