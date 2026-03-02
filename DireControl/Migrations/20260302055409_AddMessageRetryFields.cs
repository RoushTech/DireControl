using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DireControl.Migrations
{
    /// <inheritdoc />
    public partial class AddMessageRetryFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastSentAt",
                table: "Messages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxRetries",
                table: "Messages",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextRetryAt",
                table: "Messages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RetryCount",
                table: "Messages",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RetryState",
                table: "Messages",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Messages_RetryState_NextRetryAt",
                table: "Messages",
                columns: new[] { "RetryState", "NextRetryAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Messages_RetryState_NextRetryAt",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "LastSentAt",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "MaxRetries",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "NextRetryAt",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "RetryCount",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "RetryState",
                table: "Messages");
        }
    }
}
