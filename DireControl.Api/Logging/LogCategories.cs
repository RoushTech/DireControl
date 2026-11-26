namespace DireControl.Api.Logging;

/// <summary>
/// Named logger categories for output an operator may want to turn on or off
/// independently of the class that produces it.
///
/// Logging normally takes its category from the declaring type, which couples
/// unrelated output together: silencing chatty per-burst diagnostics would also
/// silence the modem's start-up and transmit logging from the same class.  These
/// categories are stable strings so a level override stored against them keeps
/// working across refactors, and they appear in the Logs view's level editor.
/// </summary>
public static class LogCategories
{
    /// <summary>Per-carrier-burst channel activity: level, duration, decode outcome.</summary>
    public const string ChannelActivity = "DireControl.ChannelActivity";

    /// <summary>Off-air audio capture: files written, and failures to write them.</summary>
    public const string AudioCapture = "DireControl.AudioCapture";
}
