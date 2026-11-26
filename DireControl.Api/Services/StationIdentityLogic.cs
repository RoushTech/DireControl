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

        if (setting.MaxRetryAttempts is { } attempts)
            options.MaxRetryAttempts = Math.Clamp(attempts, MinRetryAttempts, MaxRetryAttemptsLimit);
        if (setting.InitialRetryDelaySeconds is { } delay)
            options.InitialRetryDelaySeconds = Math.Clamp(delay, MinRetryDelaySeconds, MaxRetryDelaySeconds);
    }

    /// <summary>
    /// Why nothing may be transmitted under <paramref name="callsign"/>, or null.
    /// N0CALL is a placeholder: keying a transmitter without a licensed
    /// callsign is never allowed, PTT tests and calibration tones included.
    /// </summary>
    public static string? TransmitBlockedReason(string? callsign) =>
        string.IsNullOrWhiteSpace(callsign) || callsign.Trim().StartsWith("N0CALL", StringComparison.OrdinalIgnoreCase)
            ? "Transmit is blocked until the station callsign is set (it is still N0CALL)."
            : null;

    public const int MinRetryAttempts = 1;
    public const int MaxRetryAttemptsLimit = 10;
    public const int MinRetryDelaySeconds = 10;
    public const int MaxRetryDelaySeconds = 600;

    /// <summary>Validation message for a retry-settings update, or null when valid.</summary>
    public static string? ValidateRetry(int maxRetryAttempts, int initialRetryDelaySeconds) =>
        maxRetryAttempts is < MinRetryAttempts or > MaxRetryAttemptsLimit
            ? $"Retry attempts must be between {MinRetryAttempts} and {MaxRetryAttemptsLimit}."
            : initialRetryDelaySeconds is < MinRetryDelaySeconds or > MaxRetryDelaySeconds
                ? $"Initial retry delay must be between {MinRetryDelaySeconds} and {MaxRetryDelaySeconds} seconds."
                : null;
}
