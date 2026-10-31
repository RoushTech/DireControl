using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DireControl.Migrations
{
    /// <inheritdoc />
    public partial class AddMessageReceiveCount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastReceivedAt",
                table: "Messages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReceiveCount",
                table: "Messages",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastReceivedAt",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "ReceiveCount",
                table: "Messages");
        }
    }
}
