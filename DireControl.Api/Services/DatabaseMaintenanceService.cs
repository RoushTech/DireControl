using System.Data;
using System.Data.Common;
using DireControl.Data;
using DireControl.Data.Models;
using DireControl.Enums;
using Microsoft.EntityFrameworkCore;

namespace DireControl.Api.Services;

/// <summary>
/// Runs database cleanup: prunes old <see cref="Packet"/> rows per-source according
/// to the retention windows in <see cref="UserSetting"/>, then optionally VACUUMs to
/// hand reclaimed space back to the filesystem. A single run executes at a time
/// (shared by the scheduler and manual triggers); deletes are batched so ingestion
/// is never blocked for long.
/// </summary>
public sealed class DatabaseMaintenanceService(
    IServiceScopeFactory scopeFactory,
    IHostApplicationLifetime lifetime,
    ILogger<DatabaseMaintenanceService> logger)
{
    private const int BatchSize = 50_000;

    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsRunning { get; private set; }
    public CleanupResult? LastResult { get; private set; }

    /// <summary>
    /// Starts a cleanup run in the background if one is not already in progress.
    /// Returns false (without starting another) when a run is already active.
    /// </summary>
    public bool TryStart(bool vacuum)
    {
        if (!_gate.Wait(0))
            return false;

        IsRunning = true;
        _ = Task.Run(async () =>
        {
            try
            {
                LastResult = await RunCoreAsync(vacuum, lifetime.ApplicationStopping);
            }
            catch (OperationCanceledException)
            {
                // App shutting down — leave the previous LastResult in place.
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Database cleanup failed.");
                LastResult = new CleanupResult { CompletedAt = DateTime.UtcNow, Error = ex.Message };
            }
            finally
            {
                IsRunning = false;
                _gate.Release();
            }
        });

        return true;
    }

    private async Task<CleanupResult> RunCoreAsync(bool vacuum, CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
        var settings = await db.UserSettings.FindAsync([1], ct) ?? new UserSetting { Id = 1 };

        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync(ct);

        // Wait (rather than fail) when ingestion holds a write lock.
        await ExecAsync(conn, "PRAGMA busy_timeout=120000;", ct);

        var sizeBefore = await GetSizeBytesAsync(conn, ct);

        logger.LogInformation(
            "Database cleanup started (retention days — RF: {Rf}, APRS-IS: {Is}, Own: {Own}).",
            RetentionLabel(settings.PacketRetentionRfDays),
            RetentionLabel(settings.PacketRetentionAprsIsDays),
            RetentionLabel(settings.PacketRetentionOwnDays));

        var rfDeleted = await PruneAsync(conn, PacketSource.Rf, settings.PacketRetentionRfDays, ct);
        var isDeleted = await PruneAsync(conn, PacketSource.AprsIs, settings.PacketRetentionAprsIsDays, ct);
        var ownDeleted = await PruneAsync(conn, PacketSource.Own, settings.PacketRetentionOwnDays, ct);
        var totalDeleted = rfDeleted + isDeleted + ownDeleted;

        var vacuumed = false;
        string? vacuumError = null;
        if (vacuum && totalDeleted > 0)
        {
            try
            {
                await ExecAsync(conn, "VACUUM;", ct);
                vacuumed = true;
            }
            catch (Exception ex)
            {
                vacuumError = ex.Message;
                logger.LogWarning(ex, "VACUUM failed during cleanup (database busy?). Pruning still succeeded.");
            }
        }

        var sizeAfter = await GetSizeBytesAsync(conn, ct);

        var result = new CleanupResult
        {
            StartedAt = startedAt,
            CompletedAt = DateTime.UtcNow,
            RfDeleted = rfDeleted,
            AprsIsDeleted = isDeleted,
            OwnDeleted = ownDeleted,
            Vacuumed = vacuumed,
            VacuumError = vacuumError,
            SizeBeforeBytes = sizeBefore,
            SizeAfterBytes = sizeAfter,
        };

        logger.LogInformation(
            "Database cleanup complete: deleted {Total} packets ({Rf} RF, {Is} APRS-IS, {Own} Own); {Before} → {After}{Vac}.",
            totalDeleted, rfDeleted, isDeleted, ownDeleted,
            FormatBytes(sizeBefore), FormatBytes(sizeAfter),
            vacuumed ? " (vacuumed)" : "");

        return result;
    }

    /// <summary>
    /// Deletes packets of <paramref name="source"/> older than <paramref name="retentionDays"/>,
    /// in batches, so each DELETE is a short transaction. Returns the number deleted.
    /// A retention of 0 (or less) means keep forever — nothing is deleted.
    /// </summary>
    private static async Task<int> PruneAsync(DbConnection conn, PacketSource source, int retentionDays, CancellationToken ct)
    {
        if (retentionDays <= 0)
            return 0;

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        var total = 0;

        while (!ct.IsCancellationRequested)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "DELETE FROM Packets WHERE Id IN " +
                "(SELECT Id FROM Packets WHERE Source = $src AND ReceivedAt < $cut LIMIT $lim);";
            AddParam(cmd, "$src", (int)source);
            AddParam(cmd, "$cut", cutoff);
            AddParam(cmd, "$lim", BatchSize);

            var deleted = await cmd.ExecuteNonQueryAsync(ct);
            total += deleted;

            if (deleted < BatchSize)
                break;
        }

        return total;
    }

    /// <summary>
    /// Deletes <see cref="Station"/> rows that have no packets and are not on the watch
    /// list, in batches. Orphans accumulate when packet retention prunes a station's last
    /// packet, and when reprocessing repairs a corrupted StationCallsign (leaving the old
    /// garbage station behind). The cascade FK removes each station's StationStatistic.
    /// Returns the number of stations deleted.
    /// </summary>
    public async Task<int> DeleteOrphanStationsAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();

        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync(ct);

        await ExecAsync(conn, "PRAGMA busy_timeout=120000;", ct);
        // Ensure the StationStatistic cascade fires on the raw DELETE below, regardless
        // of how this connection was opened (foreign keys default off in SQLite).
        await ExecAsync(conn, "PRAGMA foreign_keys=ON;", ct);

        var total = 0;

        while (!ct.IsCancellationRequested)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "DELETE FROM Stations WHERE Callsign IN " +
                "(SELECT s.Callsign FROM Stations s " +
                " WHERE s.IsOnWatchList = 0 " +
                "   AND NOT EXISTS (SELECT 1 FROM Packets p WHERE p.StationCallsign = s.Callsign) " +
                " LIMIT $lim);";
            AddParam(cmd, "$lim", BatchSize);

            var deleted = await cmd.ExecuteNonQueryAsync(ct);
            total += deleted;

            if (deleted < BatchSize)
                break;
        }

        if (total > 0)
            logger.LogInformation("Deleted {Count} orphan stations (no packets, not watch-listed).", total);

        return total;
    }

    /// <summary>Current database file size in bytes (page_count × page_size).</summary>
    public async Task<long> GetCurrentSizeBytesAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync(ct);
        return await GetSizeBytesAsync(conn, ct);
    }

    private static async Task<long> GetSizeBytesAsync(DbConnection conn, CancellationToken ct)
    {
        var pageCount = await ScalarLongAsync(conn, "PRAGMA page_count;", ct);
        var pageSize = await ScalarLongAsync(conn, "PRAGMA page_size;", ct);
        return pageCount * pageSize;
    }

    private static async Task ExecAsync(DbConnection conn, string sql, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> ScalarLongAsync(DbConnection conn, string sql, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null ? 0 : Convert.ToInt64(result);
    }

    private static void AddParam(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    private static string RetentionLabel(int days) => days <= 0 ? "off" : days.ToString();

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:0.0} {units[unit]}";
    }
}

/// <summary>Outcome of a single database cleanup run.</summary>
public sealed class CleanupResult
{
    public DateTime StartedAt { get; init; }
    public DateTime CompletedAt { get; init; }
    public int RfDeleted { get; init; }
    public int AprsIsDeleted { get; init; }
    public int OwnDeleted { get; init; }
    public bool Vacuumed { get; init; }
    public string? VacuumError { get; init; }
    public long SizeBeforeBytes { get; init; }
    public long SizeAfterBytes { get; init; }
    public string? Error { get; init; }
}
