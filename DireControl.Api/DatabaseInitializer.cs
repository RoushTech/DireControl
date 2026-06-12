using DireControl.Data;
using Microsoft.EntityFrameworkCore;

namespace DireControl.Api;

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DireControlContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<DireControlContext>>();

        var migrations = context.Database.GetMigrations();
        if (migrations.Any())
        {
            logger.LogInformation("Applying database migrations...");
            await context.Database.MigrateAsync();
        }
        else
        {
            logger.LogInformation("No migrations found. Ensuring database schema is created...");
            await context.Database.EnsureCreatedAsync();
        }

        // WAL lets readers (map loads, statistics) run without blocking the
        // continuous packet ingest writer. Persistent, but harmless to re-apply.
        await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");

        logger.LogInformation("Database ready.");
    }
}
