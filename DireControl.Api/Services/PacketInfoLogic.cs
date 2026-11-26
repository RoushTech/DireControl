namespace DireControl.Api.Services;

/// <summary>
/// Pure helpers for interpreting decoded APRS info fields, extracted from
/// <see cref="AprsPacketParsingService"/> so they can be unit-tested directly.
/// </summary>
internal static class PacketInfoLogic
{
    /// <summary>
    /// Renders a station capabilities dictionary back to its comma-separated form,
    /// e.g. <c>IGATE,MSG_CNT=10,LOC_CNT=97</c>, with surrounding whitespace removed.
    /// </summary>
    internal static string FormatStationCapabilities(IReadOnlyDictionary<string, string?> capabilities) =>
        string.Join(',', capabilities.Select(kv => kv.Value is null ? kv.Key : $"{kv.Key}={kv.Value}"));

    /// <summary>
    /// True when a capabilities packet announces an igate that is not disabled
    /// (some igates send <c>&lt;IGATE,DISABLED</c> while RF→IS gating is off).
    /// </summary>
    internal static bool IsActiveIgateCapabilities(IReadOnlyDictionary<string, string?> capabilities) =>
        capabilities.Keys.Any(k => k.Equals("IGATE", StringComparison.OrdinalIgnoreCase))
        && !capabilities.Keys.Any(k => k.Equals("DISABLED", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Recognises a plain-text beacon: a TNC2 packet whose info field starts with a letter
    /// or digit (i.e. has no APRS data type identifier) and is entirely printable text.
    /// Requiring the whole packet to be printable keeps corrupted RF frames, whose
    /// headers and bodies are full of control bytes, classified as unparseable.
    /// </summary>
    /// <param name="rawPacket">The raw TNC2 packet.</param>
    /// <param name="text">The beacon text with surrounding whitespace trimmed.</param>
    internal static bool TryGetPlainTextBeacon(string rawPacket, out string text)
    {
        text = string.Empty;

        var colon = rawPacket.IndexOf(':');
        var gt = rawPacket.IndexOf('>');
        if (colon <= 0 || gt <= 0 || gt > colon)
            return false;

        var info = rawPacket[(colon + 1)..].Trim();
        if (info.Length == 0 || !char.IsAsciiLetterOrDigit(info[0]))
            return false;

        if (rawPacket.TrimEnd('\r', '\n').Any(c => c < 0x20 || c > 0x7E))
            return false;

        text = info;
        return true;
    }
}
