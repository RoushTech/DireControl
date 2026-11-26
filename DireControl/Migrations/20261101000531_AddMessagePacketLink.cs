using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DireControl.Migrations
{
    /// <inheritdoc />
    public partial class AddMessagePacketLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PacketId",
                table: "Messages",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Messages_PacketId",
                table: "Messages",
                column: "PacketId");

            migrationBuilder.AddForeignKey(
                name: "FK_Messages_Packets_PacketId",
                table: "Messages",
                column: "PacketId",
                principalTable: "Packets",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Messages_Packets_PacketId",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "IX_Messages_PacketId",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "PacketId",
                table: "Messages");
        }
    }
}
