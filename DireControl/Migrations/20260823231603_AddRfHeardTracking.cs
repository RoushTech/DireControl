using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DireControl.Migrations
{
    /// <inheritdoc />
    public partial class AddRfHeardTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "HeardVia",
                table: "Packets",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "RfHeardDailies",
                columns: table => new
                {
                    ChannelNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    Day = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    UniqueDirectStations = table.Column<int>(type: "INTEGER", nullable: false),
                    NewDirectStations = table.Column<int>(type: "INTEGER", nullable: false),
                    DirectPackets = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxDirectDistanceKm = table.Column<double>(type: "REAL", nullable: true),
                    MedianDirectDistanceKm = table.Column<double>(type: "REAL", nullable: true),
                    DirectStationsWithPosition = table.Column<int>(type: "INTEGER", nullable: false),
                    LastComputedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RfHeardDailies", x => new { x.ChannelNumber, x.Day });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Packets_HeardVia_Source_ReceivedAt",
                table: "Packets",
                columns: new[] { "HeardVia", "Source", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RfHeardDailies_Day",
                table: "RfHeardDailies",
                column: "Day");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RfHeardDailies");

            migrationBuilder.DropIndex(
                name: "IX_Packets_HeardVia_Source_ReceivedAt",
                table: "Packets");

            migrationBuilder.DropColumn(
                name: "HeardVia",
                table: "Packets");
        }
    }
}
