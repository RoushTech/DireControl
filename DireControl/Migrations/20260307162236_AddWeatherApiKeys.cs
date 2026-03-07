using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DireControl.Migrations
{
    /// <inheritdoc />
    public partial class AddWeatherApiKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OpenWeatherMapApiKey",
                table: "UserSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TomorrowIoApiKey",
                table: "UserSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "UserSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "OpenWeatherMapApiKey", "TomorrowIoApiKey" },
                values: new object[] { null, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OpenWeatherMapApiKey",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "TomorrowIoApiKey",
                table: "UserSettings");
        }
    }
}
