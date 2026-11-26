using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DireControl.Migrations
{
    /// <inheritdoc />
    public partial class StationIdentityOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "HomeLat",
                table: "UserSettings",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "HomeLon",
                table: "UserSettings",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OurCallsign",
                table: "UserSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "UserSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "HomeLat", "HomeLon", "OurCallsign" },
                values: new object[] { null, null, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HomeLat",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "HomeLon",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "OurCallsign",
                table: "UserSettings");
        }
    }
}
