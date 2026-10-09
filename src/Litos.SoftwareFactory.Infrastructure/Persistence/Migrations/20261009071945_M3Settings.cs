using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Litos.SoftwareFactory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M3Settings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "factory_secrets",
                columns: table => new
                {
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Ciphertext = table.Column<string>(type: "text", nullable: false),
                    SetAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SetBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_factory_secrets", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "factory_settings",
                columns: table => new
                {
                    Section = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Json = table.Column<string>(type: "jsonb", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_factory_settings", x => x.Section);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "factory_secrets");

            migrationBuilder.DropTable(
                name: "factory_settings");
        }
    }
}
