using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Litos.SoftwareFactory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M3ModelCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "model_catalog",
                columns: table => new
                {
                    Provider = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ModelId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    ContextLength = table.Column<int>(type: "integer", nullable: true),
                    SupportsTools = table.Column<bool>(type: "boolean", nullable: true),
                    InputPricePerMillion = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    OutputPricePerMillion = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_catalog", x => new { x.Provider, x.ModelId });
                });

            migrationBuilder.CreateTable(
                name: "model_catalog_fetches",
                columns: table => new
                {
                    Provider = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    AttemptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FetchedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_catalog_fetches", x => x.Provider);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "model_catalog");

            migrationBuilder.DropTable(
                name: "model_catalog_fetches");
        }
    }
}
