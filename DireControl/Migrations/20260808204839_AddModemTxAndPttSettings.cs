using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DireControl.Migrations
{
    /// <inheritdoc />
    public partial class AddModemTxAndPttSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ModemPersistence",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ModemPlaybackDevice",
                table: "UserSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "ModemPttGpioActiveLow",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ModemPttGpioChip",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ModemPttGpioLine",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ModemPttHidDevice",
                table: "UserSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ModemPttHidPin",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ModemPttMethod",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ModemPttRigctldHost",
                table: "UserSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "ModemPttRigctldPort",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ModemPttSerialPort",
                table: "UserSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ModemPttSerialUseDtr",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ModemPttSerialUseRts",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ModemSlotTimeMs",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ModemTxAudioLevelPct",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ModemTxDelayMs",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "ModemTxEnabled",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ModemTxTailMs",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.UpdateData(
                table: "UserSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "ModemPersistence", "ModemPlaybackDevice", "ModemPttGpioActiveLow", "ModemPttGpioChip", "ModemPttGpioLine", "ModemPttHidDevice", "ModemPttHidPin", "ModemPttMethod", "ModemPttRigctldHost", "ModemPttRigctldPort", "ModemPttSerialPort", "ModemPttSerialUseDtr", "ModemPttSerialUseRts", "ModemSlotTimeMs", "ModemTxAudioLevelPct", "ModemTxDelayMs", "ModemTxEnabled", "ModemTxTailMs" },
                values: new object[] { 63, "default", false, 0, 0, null, 3, 1, "localhost", 4532, null, false, true, 100, 80, 300, false, 50 });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ModemPersistence",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "ModemPlaybackDevice",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "ModemPttGpioActiveLow",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "ModemPttGpioChip",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "ModemPttGpioLine",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "ModemPttHidDevice",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "ModemPttHidPin",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "ModemPttMethod",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "ModemPttRigctldHost",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "ModemPttRigctldPort",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "ModemPttSerialPort",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "ModemPttSerialUseDtr",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "ModemPttSerialUseRts",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "ModemSlotTimeMs",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "ModemTxAudioLevelPct",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "ModemTxDelayMs",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "ModemTxEnabled",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "ModemTxTailMs",
                table: "UserSettings");
        }
    }
}
