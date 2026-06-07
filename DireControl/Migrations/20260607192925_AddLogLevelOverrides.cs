using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DireControl.Migrations
{
    /// <inheritdoc />
    public partial class AddLogLevelOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LogLevelOverrides",
                columns: table => new
                {
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    Level = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LogLevelOverrides", x => x.Category);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LogLevelOverrides");
        }
    }
}
