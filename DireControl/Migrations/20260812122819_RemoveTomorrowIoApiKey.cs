using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DireControl.Migrations
{
    /// <inheritdoc />
    public partial class RemoveTomorrowIoApiKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TomorrowIoApiKey",
                table: "UserSettings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TomorrowIoApiKey",
                table: "UserSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "UserSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "TomorrowIoApiKey",
                value: null);
        }
    }
}
