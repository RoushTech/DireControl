using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DireControl.Migrations
{
    /// <inheritdoc />
    public partial class AddBeaconConfigToRadio : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BeaconComment",
                table: "Radios",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BeaconPath",
                table: "Radios",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BeaconSymbol",
                table: "Radios",
                type: "TEXT",
                maxLength: 2,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BeaconComment",
                table: "Radios");

            migrationBuilder.DropColumn(
                name: "BeaconPath",
                table: "Radios");

            migrationBuilder.DropColumn(
                name: "BeaconSymbol",
                table: "Radios");
        }
    }
}
