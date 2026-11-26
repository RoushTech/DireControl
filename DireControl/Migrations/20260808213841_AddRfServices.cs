using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DireControl.Migrations
{
    /// <inheritdoc />
    public partial class AddRfServices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DigipeaterEnabled",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "DigipeaterFillInOnly",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "DigipeaterMaxWideN",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IsToRfGatingEnabled",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "IsToRfPath",
                table: "UserSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "IsToRfRecentHeardMinutes",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "KissServerEnabled",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "KissServerPort",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "RfToIsGatingEnabled",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.UpdateData(
                table: "UserSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "DigipeaterEnabled", "DigipeaterFillInOnly", "DigipeaterMaxWideN", "IsToRfGatingEnabled", "IsToRfPath", "IsToRfRecentHeardMinutes", "KissServerEnabled", "KissServerPort", "RfToIsGatingEnabled" },
                values: new object[] { false, false, 2, false, "", 30, false, 8010, false });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DigipeaterEnabled",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "DigipeaterFillInOnly",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "DigipeaterMaxWideN",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "IsToRfGatingEnabled",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "IsToRfPath",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "IsToRfRecentHeardMinutes",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "KissServerEnabled",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "KissServerPort",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "RfToIsGatingEnabled",
                table: "UserSettings");
        }
    }
}
