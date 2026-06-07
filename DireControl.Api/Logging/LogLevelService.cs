using DireControl.Data;
using DireControl.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace DireControl.Api.Logging;

/// <summary>
/// Reads and writes runtime log-level overrides, persisting them to the database
/// and pushing them into <see cref="RuntimeLoggingConfigSource"/> so they take
/// effect immediately across every logging provider (console/docker and the
/// SignalR stream alike).
/// </summary>
public sealed class LogLevelService(
    RuntimeLoggingConfigSource source,
    IServiceScopeFactory scopeFactory)
{
    /// <summary>The log levels selectable in the UI, mirroring <see cref="LogLevel"/>.</summary>
    public static readonly string[] AvailableLevels =
        ["Trace", "Debug", "Information", "Warning", "Error", "Critical", "None"];

    /// <summary>Categories surfaced in the UI by default. Any category is accepted by the API.</summary>
    public static readonly string[] CommonCategories =
        ["Default", "DireControl", "Microsoft.AspNetCore", "Microsoft.EntityFrameworkCore"];

    public static bool IsValidLevel(string level) =>
        Enum.TryParse<LogLevel>(level, ignoreCase: true, out _);

    /// <summary>Loads persisted overrides from the database and applies them. Call once at startup.</summary>
    public async Task ApplyFromDatabaseAsync(CancellationToken ct = default)
    {
        source.Provider.SetLevels(await GetOverridesAsync(ct));
    }

    public async Task<Dictionary<string, string>> GetOverridesAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
        return await db.LogLevelOverrides.ToDictionaryAsync(o => o.Category, o => o.Level, ct);
    }

    /// <summary>
    /// Sets the override for <paramref name="category"/>, or removes it when
    /// <paramref name="level"/> is null/empty ("inherit appsettings"), then re-applies
    /// the full override set live.
    /// </summary>
    public async Task SetLevelAsync(string category, string? level, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();

        var existing = await db.LogLevelOverrides.FindAsync([category], ct);

        if (string.IsNullOrWhiteSpace(level))
        {
            if (existing is not null)
                db.LogLevelOverrides.Remove(existing);
        }
        else if (existing is null)
        {
            db.LogLevelOverrides.Add(new LogLevelOverride { Category = category, Level = level });
        }
        else
        {
            existing.Level = level;
        }

        await db.SaveChangesAsync(ct);

        source.Provider.SetLevels(
            await db.LogLevelOverrides.ToDictionaryAsync(o => o.Category, o => o.Level, ct));
    }
}
