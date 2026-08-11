using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DireControl.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectedMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ConnectedModeDefaultPaclen",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "ConnectedModeInboundEnabled",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ConnectedModeMaxSessions",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "ConnectedModePreferMod128",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ConnectedModeRetries",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ConnectedModeT1Seconds",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ConnectedModeWindowSize",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.UpdateData(
                table: "UserSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "ConnectedModeDefaultPaclen", "ConnectedModeInboundEnabled", "ConnectedModeMaxSessions", "ConnectedModePreferMod128", "ConnectedModeRetries", "ConnectedModeT1Seconds", "ConnectedModeWindowSize" },
                values: new object[] { 128, false, 10, false, 10, 3, 4 });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConnectedModeDefaultPaclen",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "ConnectedModeInboundEnabled",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "ConnectedModeMaxSessions",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "ConnectedModePreferMod128",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "ConnectedModeRetries",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "ConnectedModeT1Seconds",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "ConnectedModeWindowSize",
                table: "UserSettings");
        }
    }
}
