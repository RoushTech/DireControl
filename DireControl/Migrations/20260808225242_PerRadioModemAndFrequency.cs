using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DireControl.Migrations
{
    /// <inheritdoc />
    public partial class PerRadioModemAndFrequency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add the per-radio columns first, migrate the old global modem
            // configuration onto the oldest active radio, then drop the old
            // global columns.
            migrationBuilder.AddColumn<double>(
                name: "FrequencyMhz",
                table: "Radios",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Mode",
                table: "Radios",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModemCaptureDevice",
                table: "Radios",
                type: "TEXT",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "ModemEnabled",
                table: "Radios",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ModemPlaybackDevice",
                table: "Radios",
                type: "TEXT",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "PttGpioActiveLow",
                table: "Radios",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "PttGpioChip",
                table: "Radios",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PttGpioLine",
                table: "Radios",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "PttHidDevice",
                table: "Radios",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PttHidPin",
                table: "Radios",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PttMethod",
                table: "Radios",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "PttRigctldHost",
                table: "Radios",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "PttRigctldPort",
                table: "Radios",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "PttSerialPort",
                table: "Radios",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PttSerialUseDtr",
                table: "Radios",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "PttSerialUseRts",
                table: "Radios",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "TxAudioLevelPct",
                table: "Radios",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TxDelayMs",
                table: "Radios",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "TxEnabled",
                table: "Radios",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "TxPersistence",
                table: "Radios",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TxSlotTimeMs",
                table: "Radios",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TxTailMs",
                table: "Radios",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql(
                """
                UPDATE Radios SET
                    ModemEnabled = (SELECT ModemEnabled FROM UserSettings WHERE Id = 1),
                    ModemCaptureDevice = (SELECT ModemCaptureDevice FROM UserSettings WHERE Id = 1),
                    ModemPlaybackDevice = (SELECT ModemPlaybackDevice FROM UserSettings WHERE Id = 1),
                    TxEnabled = (SELECT ModemTxEnabled FROM UserSettings WHERE Id = 1),
                    TxAudioLevelPct = (SELECT ModemTxAudioLevelPct FROM UserSettings WHERE Id = 1),
                    TxDelayMs = (SELECT ModemTxDelayMs FROM UserSettings WHERE Id = 1),
                    TxTailMs = (SELECT ModemTxTailMs FROM UserSettings WHERE Id = 1),
                    TxPersistence = (SELECT ModemPersistence FROM UserSettings WHERE Id = 1),
                    TxSlotTimeMs = (SELECT ModemSlotTimeMs FROM UserSettings WHERE Id = 1),
                    PttMethod = (SELECT ModemPttMethod FROM UserSettings WHERE Id = 1),
                    PttSerialPort = (SELECT ModemPttSerialPort FROM UserSettings WHERE Id = 1),
                    PttSerialUseRts = (SELECT ModemPttSerialUseRts FROM UserSettings WHERE Id = 1),
                    PttSerialUseDtr = (SELECT ModemPttSerialUseDtr FROM UserSettings WHERE Id = 1),
                    PttHidDevice = (SELECT ModemPttHidDevice FROM UserSettings WHERE Id = 1),
                    PttHidPin = (SELECT ModemPttHidPin FROM UserSettings WHERE Id = 1),
                    PttGpioChip = (SELECT ModemPttGpioChip FROM UserSettings WHERE Id = 1),
                    PttGpioLine = (SELECT ModemPttGpioLine FROM UserSettings WHERE Id = 1),
                    PttGpioActiveLow = (SELECT ModemPttGpioActiveLow FROM UserSettings WHERE Id = 1),
                    PttRigctldHost = (SELECT ModemPttRigctldHost FROM UserSettings WHERE Id = 1),
                    PttRigctldPort = (SELECT ModemPttRigctldPort FROM UserSettings WHERE Id = 1)
                WHERE Id = (
                    SELECT Id FROM Radios WHERE IsActive = 1 ORDER BY CreatedAt LIMIT 1
                )
                AND EXISTS (SELECT 1 FROM UserSettings WHERE Id = 1 AND ModemEnabled = 1);
                """);

            migrationBuilder.DropColumn(
                name: "ModemCaptureDevice",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "ModemEnabled",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "ModemKissChannel",
                table: "UserSettings");

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FrequencyMhz",
                table: "Radios");

            migrationBuilder.DropColumn(
                name: "Mode",
                table: "Radios");

            migrationBuilder.DropColumn(
                name: "ModemCaptureDevice",
                table: "Radios");

            migrationBuilder.DropColumn(
                name: "ModemEnabled",
                table: "Radios");

            migrationBuilder.DropColumn(
                name: "ModemPlaybackDevice",
                table: "Radios");

            migrationBuilder.DropColumn(
                name: "PttGpioActiveLow",
                table: "Radios");

            migrationBuilder.DropColumn(
                name: "PttGpioChip",
                table: "Radios");

            migrationBuilder.DropColumn(
                name: "PttGpioLine",
                table: "Radios");

            migrationBuilder.DropColumn(
                name: "PttHidDevice",
                table: "Radios");

            migrationBuilder.DropColumn(
                name: "PttHidPin",
                table: "Radios");

            migrationBuilder.DropColumn(
                name: "PttMethod",
                table: "Radios");

            migrationBuilder.DropColumn(
                name: "PttRigctldHost",
                table: "Radios");

            migrationBuilder.DropColumn(
                name: "PttRigctldPort",
                table: "Radios");

            migrationBuilder.DropColumn(
                name: "PttSerialPort",
                table: "Radios");

            migrationBuilder.DropColumn(
                name: "PttSerialUseDtr",
                table: "Radios");

            migrationBuilder.DropColumn(
                name: "PttSerialUseRts",
                table: "Radios");

            migrationBuilder.DropColumn(
                name: "TxAudioLevelPct",
                table: "Radios");

            migrationBuilder.DropColumn(
                name: "TxDelayMs",
                table: "Radios");

            migrationBuilder.DropColumn(
                name: "TxEnabled",
                table: "Radios");

            migrationBuilder.DropColumn(
                name: "TxPersistence",
                table: "Radios");

            migrationBuilder.DropColumn(
                name: "TxSlotTimeMs",
                table: "Radios");

            migrationBuilder.DropColumn(
                name: "TxTailMs",
                table: "Radios");

            migrationBuilder.AddColumn<string>(
                name: "ModemCaptureDevice",
                table: "UserSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "ModemEnabled",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ModemKissChannel",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

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
                columns: new[] { "ModemCaptureDevice", "ModemEnabled", "ModemKissChannel", "ModemPersistence", "ModemPlaybackDevice", "ModemPttGpioActiveLow", "ModemPttGpioChip", "ModemPttGpioLine", "ModemPttHidDevice", "ModemPttHidPin", "ModemPttMethod", "ModemPttRigctldHost", "ModemPttRigctldPort", "ModemPttSerialPort", "ModemPttSerialUseDtr", "ModemPttSerialUseRts", "ModemSlotTimeMs", "ModemTxAudioLevelPct", "ModemTxDelayMs", "ModemTxEnabled", "ModemTxTailMs" },
                values: new object[] { "default", false, 0, 63, "default", false, 0, 0, null, 3, 1, "localhost", 4532, null, false, true, 100, 80, 300, false, 50 });
        }
    }
}
