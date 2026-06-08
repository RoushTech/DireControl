using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DireControl.Migrations
{
    /// <inheritdoc />
    public partial class AddPacketParserVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ParserVersion",
                table: "Packets",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Packets_ParserVersion",
                table: "Packets",
                column: "ParserVersion");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Packets_ParserVersion",
                table: "Packets");

            migrationBuilder.DropColumn(
                name: "ParserVersion",
                table: "Packets");
        }
    }
}
