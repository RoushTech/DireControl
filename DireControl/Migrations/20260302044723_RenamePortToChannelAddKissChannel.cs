using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DireControl.Migrations
{
    /// <inheritdoc />
    public partial class RenamePortToChannelAddKissChannel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "DirewolfPort",
                table: "Radios",
                newName: "ChannelNumber");

            migrationBuilder.AddColumn<int>(
                name: "KissChannel",
                table: "Packets",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "KissChannel",
                table: "Packets");

            migrationBuilder.RenameColumn(
                name: "ChannelNumber",
                table: "Radios",
                newName: "DirewolfPort");
        }
    }
}
