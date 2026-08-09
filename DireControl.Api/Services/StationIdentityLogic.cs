using System.Text.RegularExpressions;
using DireControl.Data.Models;

namespace DireControl.Api.Services;

/// <summary>
/// Pure logic for the station-identity settings (callsign, home position):
/// validation and applying stored <see cref="UserSetting"/> overrides onto
/// <see cref="DireControlOptions"/>.
/// </summary>
public static partial class StationIdentityLogic
{
    [GeneratedRegex(@"^[A-Z0-9]{1,6}(-(\d|1[0-5]))?$", RegexOptions.Compiled)]
    private static partial Regex CallsignRegex();

    /// <summary>Base callsign of up to 6 letters/digits with an optional -0..-15 SSID.</summary>
    public static bool IsValidCallsign(string callsign) =>
        !string.IsNullOrWhiteSpace(callsign) && CallsignRegex().IsMatch(callsign);

    /// <summary>
    /// Copies the user's stored overrides onto the live options instance.
    /// Values left null in <paramref name="setting"/> keep the configured
    /// (appsettings) values; the home position only applies as a pair.
    /// </summary>
    public static void ApplyOverrides(UserSetting setting, DireControlOptions options)
    {
        if (!string.IsNullOrWhiteSpace(setting.OurCallsign))
            options.OurCallsign = setting.OurCallsign;

        if (setting.HomeLat.HasValue && setting.HomeLon.HasValue)
        {
            options.HomeLat = setting.HomeLat;
            options.HomeLon = setting.HomeLon;
        }
    }
}
