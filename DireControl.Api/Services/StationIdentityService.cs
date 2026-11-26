using DireControl.Data;
using DireControl.Data.Models;
using Microsoft.Extensions.Options;

namespace DireControl.Api.Services;

/// <summary>
/// Applies the station-identity overrides stored in <see cref="UserSetting"/>
/// (callsign, home position) onto the live <see cref="DireControlOptions"/>
/// instance — once at startup and again whenever the settings are saved — so
/// every service that reads options at use-time picks up the current values.
/// </summary>
public class StationIdentityService(
    IServiceScopeFactory scopeFactory,
    IOptions<DireControlOptions> options)
{
    public async Task ApplyFromDatabaseAsync()
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
        var setting = await db.UserSettings.FindAsync(1);
        if (setting is not null)
            Apply(setting);
    }

    public void Apply(UserSetting setting) =>
        StationIdentityLogic.ApplyOverrides(setting, options.Value);
}
