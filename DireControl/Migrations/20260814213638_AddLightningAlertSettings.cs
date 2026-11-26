using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DireControl.Migrations
{
    /// <inheritdoc />
    public partial class AddLightningAlertSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LightningAlertCooldownMinutes",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "LightningAlertEnabled",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "LightningAlertRadiusKm",
                table: "UserSettings",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.UpdateData(
                table: "UserSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "LightningAlertCooldownMinutes", "LightningAlertEnabled", "LightningAlertRadiusKm" },
                values: new object[] { 5, false, 30.0 });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LightningAlertCooldownMinutes",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "LightningAlertEnabled",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "LightningAlertRadiusKm",
                table: "UserSettings");
        }
    }
}
