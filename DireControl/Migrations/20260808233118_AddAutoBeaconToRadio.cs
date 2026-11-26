using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DireControl.Migrations
{
    /// <inheritdoc />
    public partial class AddAutoBeaconToRadio : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoBeaconEnabled",
                table: "Radios",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "AutoBeaconIntervalSeconds",
                table: "Radios",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1800);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoBeaconEnabled",
                table: "Radios");

            migrationBuilder.DropColumn(
                name: "AutoBeaconIntervalSeconds",
                table: "Radios");
        }
    }
}
