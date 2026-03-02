using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DireControl.Migrations
{
    /// <inheritdoc />
    public partial class AddRadioBeaconTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Radios",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Callsign = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Ssid = table.Column<string>(type: "TEXT", maxLength: 4, nullable: true),
                    FullCallsign = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    DirewolfPort = table.Column<int>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpectedIntervalSeconds = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Radios", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OwnBeacons",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RadioId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    BeaconedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Latitude = table.Column<double>(type: "REAL", nullable: true),
                    Longitude = table.Column<double>(type: "REAL", nullable: true),
                    Heading = table.Column<int>(type: "INTEGER", nullable: true),
                    Speed = table.Column<double>(type: "REAL", nullable: true),
                    Comment = table.Column<string>(type: "TEXT", nullable: true),
                    PathUsed = table.Column<string>(type: "TEXT", nullable: true),
                    HopCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnBeacons", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OwnBeacons_Radios_RadioId",
                        column: x => x.RadioId,
                        principalTable: "Radios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DigiConfirmations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnBeaconId = table.Column<int>(type: "INTEGER", nullable: false),
                    ConfirmedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DigipeaterCallsign = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    DigipeaterLat = table.Column<double>(type: "REAL", nullable: true),
                    DigipeaterLon = table.Column<double>(type: "REAL", nullable: true),
                    AliasUsed = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    SecondsAfterBeacon = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DigiConfirmations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DigiConfirmations_OwnBeacons_OwnBeaconId",
                        column: x => x.OwnBeaconId,
                        principalTable: "OwnBeacons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DigiConfirmations_OwnBeaconId",
                table: "DigiConfirmations",
                column: "OwnBeaconId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnBeacons_RadioId_BeaconedAt",
                table: "OwnBeacons",
                columns: new[] { "RadioId", "BeaconedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Radios_FullCallsign",
                table: "Radios",
                column: "FullCallsign");

            migrationBuilder.CreateIndex(
                name: "IX_Radios_IsActive",
                table: "Radios",
                column: "IsActive");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DigiConfirmations");

            migrationBuilder.DropTable(
                name: "OwnBeacons");

            migrationBuilder.DropTable(
                name: "Radios");
        }
    }
}
