using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DireControl.Migrations
{
    /// <inheritdoc />
    public partial class AddAprsIsSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AprsIsEnabled",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "AprsIsFilter",
                table: "UserSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AprsIsHost",
                table: "UserSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "AprsIsPasscode",
                table: "UserSettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AprsIsPort",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DeduplicationWindowSeconds",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastHeardAprsIs",
                table: "Stations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastHeardRf",
                table: "Stations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InfoField",
                table: "Packets",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "Source",
                table: "Packets",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.UpdateData(
                table: "UserSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "AprsIsEnabled", "AprsIsFilter", "AprsIsHost", "AprsIsPasscode", "AprsIsPort", "DeduplicationWindowSeconds" },
                values: new object[] { false, "r/39.0/-98.0/500 t/m", "rotate.aprs2.net", null, 14580, 60 });

            migrationBuilder.CreateIndex(
                name: "IX_Packets_StationCallsign_ReceivedAt",
                table: "Packets",
                columns: new[] { "StationCallsign", "ReceivedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Packets_StationCallsign_ReceivedAt",
                table: "Packets");

            migrationBuilder.DropColumn(
                name: "AprsIsEnabled",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "AprsIsFilter",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "AprsIsHost",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "AprsIsPasscode",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "AprsIsPort",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "DeduplicationWindowSeconds",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "LastHeardAprsIs",
                table: "Stations");

            migrationBuilder.DropColumn(
                name: "LastHeardRf",
                table: "Stations");

            migrationBuilder.DropColumn(
                name: "InfoField",
                table: "Packets");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "Packets");
        }
    }
}
