using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DireControl.Migrations
{
    /// <inheritdoc />
    public partial class AddPacketTerminal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AgwpeServerBindAddress",
                table: "UserSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "AgwpeServerEnabled",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "AgwpeServerPort",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "PmsBannerText",
                table: "UserSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "PmsEnabled",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "PmsRetentionDays",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PmsSsid",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TerminalTranscriptRetentionDays",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "PmsMessages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    FromCallsign = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ToCallsign = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReadAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsKilled = table.Column<bool>(type: "INTEGER", nullable: false),
                    KilledAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Origin = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PmsMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TerminalMacros",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Label = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    PayloadBase64 = table.Column<string>(type: "TEXT", nullable: false),
                    AppendCr = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TerminalMacros", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TerminalPresets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    RemoteCallsign = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Channel = table.Column<int>(type: "INTEGER", nullable: false),
                    DigiPath = table.Column<string>(type: "TEXT", nullable: false),
                    LocalCallsign = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    Params = table.Column<string>(type: "TEXT", nullable: true),
                    IsPinned = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UseCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TerminalPresets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TerminalSessionRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Origin = table.Column<int>(type: "INTEGER", nullable: false),
                    Channel = table.Column<int>(type: "INTEGER", nullable: false),
                    LocalCallsign = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    RemoteCallsign = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    DigiPath = table.Column<string>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EndReason = table.Column<string>(type: "TEXT", nullable: true),
                    BytesIn = table.Column<long>(type: "INTEGER", nullable: false),
                    BytesOut = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TerminalSessionRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TerminalTranscriptChunks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TerminalSessionRecordId = table.Column<int>(type: "INTEGER", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Direction = table.Column<int>(type: "INTEGER", nullable: false),
                    Data = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TerminalTranscriptChunks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TerminalTranscriptChunks_TerminalSessionRecords_TerminalSessionRecordId",
                        column: x => x.TerminalSessionRecordId,
                        principalTable: "TerminalSessionRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.UpdateData(
                table: "UserSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "AgwpeServerBindAddress", "AgwpeServerEnabled", "AgwpeServerPort", "PmsBannerText", "PmsEnabled", "PmsRetentionDays", "PmsSsid", "TerminalTranscriptRetentionDays" },
                values: new object[] { "127.0.0.1", false, 8000, "Welcome to the DireControl mailbox. H for help.", false, 0, 1, 90 });

            migrationBuilder.CreateIndex(
                name: "IX_PmsMessages_CreatedAt",
                table: "PmsMessages",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PmsMessages_IsKilled",
                table: "PmsMessages",
                column: "IsKilled");

            migrationBuilder.CreateIndex(
                name: "IX_PmsMessages_ToCallsign",
                table: "PmsMessages",
                column: "ToCallsign");

            migrationBuilder.CreateIndex(
                name: "IX_TerminalMacros_SortOrder",
                table: "TerminalMacros",
                column: "SortOrder");

            migrationBuilder.CreateIndex(
                name: "IX_TerminalPresets_RemoteCallsign_Channel_DigiPath",
                table: "TerminalPresets",
                columns: new[] { "RemoteCallsign", "Channel", "DigiPath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TerminalSessionRecords_SessionId",
                table: "TerminalSessionRecords",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_TerminalSessionRecords_StartedAt",
                table: "TerminalSessionRecords",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_TerminalTranscriptChunks_TerminalSessionRecordId_Id",
                table: "TerminalTranscriptChunks",
                columns: new[] { "TerminalSessionRecordId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PmsMessages");

            migrationBuilder.DropTable(
                name: "TerminalMacros");

            migrationBuilder.DropTable(
                name: "TerminalPresets");

            migrationBuilder.DropTable(
                name: "TerminalTranscriptChunks");

            migrationBuilder.DropTable(
                name: "TerminalSessionRecords");

            migrationBuilder.DropColumn(
                name: "AgwpeServerBindAddress",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "AgwpeServerEnabled",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "AgwpeServerPort",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "PmsBannerText",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "PmsEnabled",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "PmsRetentionDays",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "PmsSsid",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "TerminalTranscriptRetentionDays",
                table: "UserSettings");
        }
    }
}
