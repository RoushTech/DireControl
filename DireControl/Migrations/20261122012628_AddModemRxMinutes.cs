using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DireControl.Migrations
{
    /// <inheritdoc />
    public partial class AddModemRxMinutes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ModemRxMinutes",
                columns: table => new
                {
                    RadioId = table.Column<string>(type: "TEXT", nullable: false),
                    Minute = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DecodedFrames = table.Column<int>(type: "INTEGER", nullable: false),
                    InvalidFrames = table.Column<int>(type: "INTEGER", nullable: false),
                    RepairedFrames = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModemRxMinutes", x => new { x.RadioId, x.Minute });
                });

            migrationBuilder.CreateIndex(
                name: "IX_ModemRxMinutes_Minute",
                table: "ModemRxMinutes",
                column: "Minute");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ModemRxMinutes");
        }
    }
}
