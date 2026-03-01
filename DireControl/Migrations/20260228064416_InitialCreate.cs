using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DireControl.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Alerts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AlertType = table.Column<int>(type: "INTEGER", nullable: false),
                    Callsign = table.Column<string>(type: "TEXT", nullable: false),
                    TriggeredAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Detail = table.Column<string>(type: "TEXT", nullable: false),
                    IsAcknowledged = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Alerts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Geofences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    CenterLat = table.Column<double>(type: "REAL", nullable: false),
                    CenterLon = table.Column<double>(type: "REAL", nullable: false),
                    RadiusMeters = table.Column<double>(type: "REAL", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    AlertOnEnter = table.Column<bool>(type: "INTEGER", nullable: false),
                    AlertOnExit = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Geofences", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Messages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FromCallsign = table.Column<string>(type: "TEXT", nullable: false),
                    ToCallsign = table.Column<string>(type: "TEXT", nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    MessageId = table.Column<string>(type: "TEXT", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsRead = table.Column<bool>(type: "INTEGER", nullable: false),
                    AckSent = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReplySent = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Messages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Stations",
                columns: table => new
                {
                    Callsign = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    FirstSeen = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeen = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastLat = table.Column<double>(type: "REAL", nullable: true),
                    LastLon = table.Column<double>(type: "REAL", nullable: true),
                    LastHeading = table.Column<int>(type: "INTEGER", nullable: true),
                    LastSpeed = table.Column<double>(type: "REAL", nullable: true),
                    LastAltitude = table.Column<double>(type: "REAL", nullable: true),
                    Symbol = table.Column<string>(type: "TEXT", maxLength: 2, nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    IsWeatherStation = table.Column<bool>(type: "INTEGER", nullable: false),
                    StationType = table.Column<int>(type: "INTEGER", nullable: false),
                    QrzLookupData = table.Column<string>(type: "TEXT", nullable: true),
                    IsOnWatchList = table.Column<bool>(type: "INTEGER", nullable: false),
                    GridSquare = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Stations", x => x.Callsign);
                });

            migrationBuilder.CreateTable(
                name: "Packets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StationCallsign = table.Column<string>(type: "TEXT", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RawPacket = table.Column<string>(type: "TEXT", nullable: false),
                    ParsedType = table.Column<int>(type: "INTEGER", nullable: false),
                    Latitude = table.Column<double>(type: "REAL", nullable: true),
                    Longitude = table.Column<double>(type: "REAL", nullable: true),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    ResolvedPath = table.Column<string>(type: "TEXT", nullable: false),
                    HopCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Comment = table.Column<string>(type: "TEXT", nullable: false),
                    WeatherData = table.Column<string>(type: "TEXT", nullable: true),
                    TelemetryData = table.Column<string>(type: "TEXT", nullable: true),
                    MessageData = table.Column<string>(type: "TEXT", nullable: true),
                    SignalData = table.Column<string>(type: "TEXT", nullable: true),
                    GridSquare = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Packets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Packets_Stations_StationCallsign",
                        column: x => x.StationCallsign,
                        principalTable: "Stations",
                        principalColumn: "Callsign",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StationStatistics",
                columns: table => new
                {
                    Callsign = table.Column<string>(type: "TEXT", nullable: false),
                    PacketsToday = table.Column<int>(type: "INTEGER", nullable: false),
                    AveragePacketsPerHour = table.Column<double>(type: "REAL", nullable: false),
                    LongestGapMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    LastComputedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StationStatistics", x => x.Callsign);
                    table.ForeignKey(
                        name: "FK_StationStatistics_Stations_Callsign",
                        column: x => x.Callsign,
                        principalTable: "Stations",
                        principalColumn: "Callsign",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_IsAcknowledged",
                table: "Alerts",
                column: "IsAcknowledged");

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_TriggeredAt",
                table: "Alerts",
                column: "TriggeredAt");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ReceivedAt",
                table: "Messages",
                column: "ReceivedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Packets_ReceivedAt",
                table: "Packets",
                column: "ReceivedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Packets_StationCallsign",
                table: "Packets",
                column: "StationCallsign");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Alerts");

            migrationBuilder.DropTable(
                name: "Geofences");

            migrationBuilder.DropTable(
                name: "Messages");

            migrationBuilder.DropTable(
                name: "Packets");

            migrationBuilder.DropTable(
                name: "StationStatistics");

            migrationBuilder.DropTable(
                name: "Stations");
        }
    }
}
