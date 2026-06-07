using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DireControl.Migrations
{
    /// <inheritdoc />
    public partial class AddPacketRetentionSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PacketRetentionAprsIsDays",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PacketRetentionOwnDays",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PacketRetentionRfDays",
                table: "UserSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.UpdateData(
                table: "UserSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "PacketRetentionAprsIsDays", "PacketRetentionOwnDays", "PacketRetentionRfDays" },
                values: new object[] { 14, 0, 0 });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PacketRetentionAprsIsDays",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "PacketRetentionOwnDays",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "PacketRetentionRfDays",
                table: "UserSettings");
        }
    }
}
