using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DireControl.Migrations
{
    /// <inheritdoc />
    public partial class MoveExternalTncSettingsToDb : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DirewolfEnabled",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "DirewolfHost",
                table: "UserSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "DirewolfPort",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DirewolfReconnectDelaySeconds",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.UpdateData(
                table: "UserSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "DirewolfEnabled", "DirewolfHost", "DirewolfPort", "DirewolfReconnectDelaySeconds" },
                values: new object[] { false, "localhost", 8001, 5 });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DirewolfEnabled",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "DirewolfHost",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "DirewolfPort",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "DirewolfReconnectDelaySeconds",
                table: "UserSettings");
        }
    }
}
