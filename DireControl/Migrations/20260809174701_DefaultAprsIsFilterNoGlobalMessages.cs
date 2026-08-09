using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DireControl.Migrations
{
    /// <inheritdoc />
    public partial class DefaultAprsIsFilterNoGlobalMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Drop the unscoped t/m term from the seeded default — it pulls every
            // message packet on the APRS-IS network, not just the range filter's.
            // Only rows still holding the old default are touched; a customized
            // filter is user data and must survive.
            migrationBuilder.Sql(
                """
                UPDATE "UserSettings"
                SET "AprsIsFilter" = 'r/39.0/-98.0/500'
                WHERE "Id" = 1 AND "AprsIsFilter" = 'r/39.0/-98.0/500 t/m';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE "UserSettings"
                SET "AprsIsFilter" = 'r/39.0/-98.0/500 t/m'
                WHERE "Id" = 1 AND "AprsIsFilter" = 'r/39.0/-98.0/500';
                """);
        }
    }
}
