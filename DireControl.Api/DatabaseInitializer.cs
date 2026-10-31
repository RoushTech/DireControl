using DireControl.Api.Services;
using DireControl.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

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

        logger.LogInformation("Database ready.");
    }

    /// <summary>
    /// Folds inbox rows that were stored as separate messages before reply-ack
    /// parsing and retransmission counting existed.  Must run after the station
    /// identity overrides are applied so our own callsign is known.
    /// </summary>
    public static async Task RepairInboxAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DireControlContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<DireControlContext>>();
        var ourCallsign = scope.ServiceProvider.GetRequiredService<IOptions<DireControlOptions>>().Value.OurCallsign;

        if (string.IsNullOrWhiteSpace(ourCallsign))
            return;

        var removed = await MessageHandlingLogic.RepairInboxAsync(context, ourCallsign, CancellationToken.None);
        if (removed > 0)
            logger.LogInformation("Merged {Count} retransmitted inbox messages into their originals.", removed);
    }
}
