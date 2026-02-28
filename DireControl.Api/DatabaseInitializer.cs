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

        logger.LogInformation("Applying database migrations...");
        await context.Database.MigrateAsync();
        logger.LogInformation("Database ready.");
    }
}
