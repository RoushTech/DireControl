using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DireControl.Migrations
{
    /// <inheritdoc />
    public partial class AddAggregatedStatisticsTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CoverageGridStatistics",
                columns: table => new
                {
                    GridSquare = table.Column<string>(type: "TEXT", nullable: false),
                    PacketCount = table.Column<int>(type: "INTEGER", nullable: false),
                    AvgLat = table.Column<double>(type: "REAL", nullable: false),
                    AvgLon = table.Column<double>(type: "REAL", nullable: false),
                    LastComputedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoverageGridStatistics", x => x.GridSquare);
                });

            migrationBuilder.CreateTable(
                name: "DigipeaterStatistics",
                columns: table => new
                {
                    Callsign = table.Column<string>(type: "TEXT", nullable: false),
                    TotalPacketsForwarded = table.Column<int>(type: "INTEGER", nullable: false),
                    Last24hPackets = table.Column<int>(type: "INTEGER", nullable: false),
                    HopPositionSum = table.Column<double>(type: "REAL", nullable: false),
                    HopPositionCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastComputedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DigipeaterStatistics", x => x.Callsign);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CoverageGridStatistics");

            migrationBuilder.DropTable(
                name: "DigipeaterStatistics");
        }
    }
}
