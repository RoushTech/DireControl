using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DireControl.Migrations
{
    /// <summary>
    /// StationType renumbered so Unknown = 0 (enum convention). Old values:
    /// Fixed=0 Mobile=1 Weather=2 Digipeater=3 IGate=4 Unknown=5 Gateway=6.
    /// New values: Unknown=0 Fixed=1 Mobile=2 Weather=3 Digipeater=4 IGate=5
    /// Gateway=6. Stored ints are remapped in one CASE so no row maps twice.
    /// </summary>
    public partial class StationTypeUnknownZero : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE Stations SET StationType = CASE StationType
                    WHEN 0 THEN 1
                    WHEN 1 THEN 2
                    WHEN 2 THEN 3
                    WHEN 3 THEN 4
                    WHEN 4 THEN 5
                    WHEN 5 THEN 0
                    ELSE StationType
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE Stations SET StationType = CASE StationType
                    WHEN 1 THEN 0
                    WHEN 2 THEN 1
                    WHEN 3 THEN 2
                    WHEN 4 THEN 3
                    WHEN 5 THEN 4
                    WHEN 0 THEN 5
                    ELSE StationType
                END;
                """);
        }
    }
}
