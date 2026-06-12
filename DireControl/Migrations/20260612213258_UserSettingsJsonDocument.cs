using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DireControl.Migrations
{
    /// <summary>
    /// Collapses UserSettings to Id + a JSON Settings document (values copied from
    /// the old columns; AprsIsPort is dropped — the APRS-IS client has no port
    /// parameter), and drops the never-populated Messages.ReplySent column.
    /// </summary>
    public partial class UserSettingsJsonDocument : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // New JSON document column, then copy the existing settings into it
            // while the old columns still exist. Booleans must be emitted as JSON
            // true/false (the column stores 0/1), hence the json(CASE ...) dance.
            migrationBuilder.AddColumn<string>(
                name: "Settings",
                table: "UserSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.Sql("""
                UPDATE UserSettings SET Settings = json_object(
                    'OutboundPath', OutboundPath,
                    'AprsIsEnabled', json(CASE WHEN AprsIsEnabled THEN 'true' ELSE 'false' END),
                    'AprsIsHost', AprsIsHost,
                    'AprsIsPasscode', AprsIsPasscode,
                    'AprsIsFilter', AprsIsFilter,
                    'DeduplicationWindowSeconds', DeduplicationWindowSeconds,
                    'PacketRetentionRfDays', PacketRetentionRfDays,
                    'PacketRetentionAprsIsDays', PacketRetentionAprsIsDays,
                    'PacketRetentionOwnDays', PacketRetentionOwnDays,
                    'OpenWeatherMapApiKey', OpenWeatherMapApiKey,
                    'TomorrowIoApiKey', TomorrowIoApiKey,
                    'RadarProvider', RadarProvider,
                    'RainViewerProApiKey', RainViewerProApiKey
                );
                """);

            migrationBuilder.DropColumn(name: "OutboundPath", table: "UserSettings");
            migrationBuilder.DropColumn(name: "AprsIsEnabled", table: "UserSettings");
            migrationBuilder.DropColumn(name: "AprsIsHost", table: "UserSettings");
            migrationBuilder.DropColumn(name: "AprsIsPasscode", table: "UserSettings");
            migrationBuilder.DropColumn(name: "AprsIsPort", table: "UserSettings");
            migrationBuilder.DropColumn(name: "AprsIsFilter", table: "UserSettings");
            migrationBuilder.DropColumn(name: "DeduplicationWindowSeconds", table: "UserSettings");
            migrationBuilder.DropColumn(name: "PacketRetentionRfDays", table: "UserSettings");
            migrationBuilder.DropColumn(name: "PacketRetentionAprsIsDays", table: "UserSettings");
            migrationBuilder.DropColumn(name: "PacketRetentionOwnDays", table: "UserSettings");
            migrationBuilder.DropColumn(name: "OpenWeatherMapApiKey", table: "UserSettings");
            migrationBuilder.DropColumn(name: "TomorrowIoApiKey", table: "UserSettings");
            migrationBuilder.DropColumn(name: "RadarProvider", table: "UserSettings");
            migrationBuilder.DropColumn(name: "RainViewerProApiKey", table: "UserSettings");

            // Never populated — no reply-tracking write path exists.
            migrationBuilder.DropColumn(name: "ReplySent", table: "Messages");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ReplySent",
                table: "Messages",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Recreate the flat settings columns and restore their values from the
            // JSON document before dropping it.
            migrationBuilder.AddColumn<string>(name: "OutboundPath", table: "UserSettings", type: "TEXT", nullable: false, defaultValue: "WIDE1-1,WIDE2-1");
            migrationBuilder.AddColumn<bool>(name: "AprsIsEnabled", table: "UserSettings", type: "INTEGER", nullable: false, defaultValue: false);
            migrationBuilder.AddColumn<string>(name: "AprsIsHost", table: "UserSettings", type: "TEXT", nullable: false, defaultValue: "rotate.aprs2.net");
            migrationBuilder.AddColumn<int>(name: "AprsIsPasscode", table: "UserSettings", type: "INTEGER", nullable: true);
            migrationBuilder.AddColumn<int>(name: "AprsIsPort", table: "UserSettings", type: "INTEGER", nullable: false, defaultValue: 14580);
            migrationBuilder.AddColumn<string>(name: "AprsIsFilter", table: "UserSettings", type: "TEXT", nullable: false, defaultValue: "r/39.0/-98.0/500 t/m");
            migrationBuilder.AddColumn<int>(name: "DeduplicationWindowSeconds", table: "UserSettings", type: "INTEGER", nullable: false, defaultValue: 60);
            migrationBuilder.AddColumn<int>(name: "PacketRetentionRfDays", table: "UserSettings", type: "INTEGER", nullable: false, defaultValue: 0);
            migrationBuilder.AddColumn<int>(name: "PacketRetentionAprsIsDays", table: "UserSettings", type: "INTEGER", nullable: false, defaultValue: 14);
            migrationBuilder.AddColumn<int>(name: "PacketRetentionOwnDays", table: "UserSettings", type: "INTEGER", nullable: false, defaultValue: 0);
            migrationBuilder.AddColumn<string>(name: "OpenWeatherMapApiKey", table: "UserSettings", type: "TEXT", nullable: true);
            migrationBuilder.AddColumn<string>(name: "TomorrowIoApiKey", table: "UserSettings", type: "TEXT", nullable: true);
            migrationBuilder.AddColumn<int>(name: "RadarProvider", table: "UserSettings", type: "INTEGER", nullable: false, defaultValue: 0);
            migrationBuilder.AddColumn<string>(name: "RainViewerProApiKey", table: "UserSettings", type: "TEXT", nullable: true);

            migrationBuilder.Sql("""
                UPDATE UserSettings SET
                    OutboundPath = COALESCE(json_extract(Settings, '$.OutboundPath'), 'WIDE1-1,WIDE2-1'),
                    AprsIsEnabled = COALESCE(json_extract(Settings, '$.AprsIsEnabled'), 0),
                    AprsIsHost = COALESCE(json_extract(Settings, '$.AprsIsHost'), 'rotate.aprs2.net'),
                    AprsIsPasscode = json_extract(Settings, '$.AprsIsPasscode'),
                    AprsIsFilter = COALESCE(json_extract(Settings, '$.AprsIsFilter'), 'r/39.0/-98.0/500 t/m'),
                    DeduplicationWindowSeconds = COALESCE(json_extract(Settings, '$.DeduplicationWindowSeconds'), 60),
                    PacketRetentionRfDays = COALESCE(json_extract(Settings, '$.PacketRetentionRfDays'), 0),
                    PacketRetentionAprsIsDays = COALESCE(json_extract(Settings, '$.PacketRetentionAprsIsDays'), 14),
                    PacketRetentionOwnDays = COALESCE(json_extract(Settings, '$.PacketRetentionOwnDays'), 0),
                    OpenWeatherMapApiKey = json_extract(Settings, '$.OpenWeatherMapApiKey'),
                    TomorrowIoApiKey = json_extract(Settings, '$.TomorrowIoApiKey'),
                    RadarProvider = COALESCE(json_extract(Settings, '$.RadarProvider'), 0),
                    RainViewerProApiKey = json_extract(Settings, '$.RainViewerProApiKey');
                """);

            migrationBuilder.DropColumn(name: "Settings", table: "UserSettings");
        }
    }
}
