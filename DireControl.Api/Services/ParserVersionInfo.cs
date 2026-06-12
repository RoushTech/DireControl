namespace DireControl.Api.Services;

/// <summary>
/// Single source of truth for the current packet-parser version. Every parse (live
/// ingest and reprocessing) stamps <see cref="DireControl.Data.Models.Packet.ParserVersion"/>
/// with <see cref="Current"/>. The reprocessor drains rows whose stored version is
/// below this value.
///
/// Bump <see cref="Current"/> whenever a change to the parsing logic should cause
/// previously-stored packets to be re-derived. History:
/// <list type="bullet">
///   <item>0 — pre-versioning rows (includes the third-party AX.25 StationCallsign bug).</item>
///   <item>1 — StationCallsign derived from the TNC2 header.</item>
/// </list>
/// </summary>
public static class ParserVersionInfo
{
    public const int Current = 1;
}
