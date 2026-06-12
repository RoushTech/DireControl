using DireControl.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DireControl.Api.Services;

/// <summary>
/// Effective station settings: the <c>UserSetting</c> row's value when set,
/// otherwise the appsettings default. Station identity (callsign, home
/// position), QRZ credentials, retry policy, and cleanup policy are editable
/// live from the UI; appsettings keeps environment config (Direwolf endpoint,
/// expiry tuning) and acts as the default layer.
/// </summary>
public sealed record StationSettings(
    string OurCallsign,
    double? HomeLat,
    double? HomeLon,
    string? QrzUsername,
    string? QrzPassword,
    int MaxRetryAttempts,
    int InitialRetryDelaySeconds,
    double DatabaseCleanupIntervalHours,
    bool VacuumOnCleanup);

/// <summary>
/// TTL-cached accessor for <see cref="StationSettings"/>. Hot paths (parsing,
/// ingest, retry) read through the cache instead of querying per packet;
/// <see cref="Invalidate"/> is called when the settings are saved so changes
/// apply on the next read.
/// </summary>
public sealed class StationSettingsProvider(
    IServiceScopeFactory scopeFactory,
    IOptions<DireControlOptions> options,
    IOptions<QrzOptions> qrzOptions)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(15);

    private StationSettings? _cached;
    private DateTime _fetchedAt = DateTime.MinValue;

    public async ValueTask<StationSettings> GetAsync(CancellationToken ct = default)
    {
        var cached = _cached;
        if (cached is not null && DateTime.UtcNow - _fetchedAt < Ttl)
            return cached;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
        var row = await db.UserSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);

        var opts = options.Value;
        var qrz = qrzOptions.Value;

        var settings = new StationSettings(
            OurCallsign: NullIfBlank(row?.OurCallsign) ?? opts.OurCallsign,
            HomeLat: row?.HomeLat ?? opts.HomeLat,
            HomeLon: row?.HomeLon ?? opts.HomeLon,
            QrzUsername: NullIfBlank(row?.QrzUsername) ?? NullIfBlank(qrz.Username),
            QrzPassword: NullIfBlank(row?.QrzPassword) ?? NullIfBlank(qrz.Password),
            MaxRetryAttempts: row?.MaxRetryAttempts ?? opts.MaxRetryAttempts,
            InitialRetryDelaySeconds: row?.InitialRetryDelaySeconds ?? opts.InitialRetryDelaySeconds,
            DatabaseCleanupIntervalHours: row?.DatabaseCleanupIntervalHours ?? opts.DatabaseCleanupIntervalHours,
            VacuumOnCleanup: row?.VacuumOnCleanup ?? opts.VacuumOnCleanup);

        _cached = settings;
        _fetchedAt = DateTime.UtcNow;
        return settings;
    }

    /// <summary>Drops the cache so the next read reflects a settings save immediately.</summary>
    public void Invalidate() => _cached = null;

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
