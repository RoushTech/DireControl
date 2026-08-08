using DireControl.Data;
using DireControl.Modem;
using DireControl.Modem.Ax25;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DireControl.Api.Services;

/// <summary>
/// WIDEn-N digipeater: examines RF-received frames (via
/// <see cref="RfFrameIngestService"/>), applies <see cref="DigipeaterLogic"/>,
/// suppresses duplicates for 30 s, and retransmits through the shared
/// <see cref="IFrameTransmitter"/>.  Never repeats our own transmissions.
/// </summary>
public sealed class DigipeaterService(
    IServiceScopeFactory scopeFactory,
    IFrameTransmitter transmitter,
    IOptions<DireControlOptions> options,
    ILogger<DigipeaterService> logger)
{
    private static readonly TimeSpan DedupWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OwnCallsignCacheTtl = TimeSpan.FromSeconds(60);

    private readonly FrameDeduper _deduper = new(DedupWindow);

    private HashSet<string> _ownCallsigns = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _ownCallsignsLoadedAt = DateTime.MinValue;

    private long _digipeatedFrames;

    /// <summary>Frames repeated since startup.</summary>
    public long DigipeatedFrames => Interlocked.Read(ref _digipeatedFrames);

    /// <summary>
    /// Considers one RF-received frame for digipeating; repeats go back out
    /// on the channel (radio) the frame was heard on.  Safe to call for
    /// every ingested frame — all filtering happens here.
    /// </summary>
    public async Task ConsiderAsync(Ax25Frame frame, int channel, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();

        var settings = await db.UserSettings.FindAsync([1], ct);
        if (settings is not { DigipeaterEnabled: true })
            return;

        // Never repeat our own traffic (station callsign or any configured radio).
        var ownCallsigns = await GetOwnCallsignsAsync(db, ct);
        if (ownCallsigns.Contains(frame.Source.ToString()))
            return;

        var rewritten = DigipeaterLogic.TryBuildDigipeat(
            frame,
            options.Value.OurCallsign,
            settings.DigipeaterMaxWideN,
            settings.DigipeaterFillInOnly);
        if (rewritten is null)
            return;

        // Duplicate suppression on source + destination + info — the path
        // mutates between hops, so it must stay out of the key.
        var key = BuildDedupKey(frame);
        if (!_deduper.IsNewFrame(key, DateTime.UtcNow))
        {
            logger.LogDebug("Digipeat suppressed (duplicate): {Source}", frame.Source);
            return;
        }

        var encoded = Ax25Encoder.Encode(rewritten);
        if (!transmitter.TrySend(encoded, channel))
        {
            logger.LogWarning("Cannot digipeat {Source}: no RF transmit backend available.", frame.Source);
            return;
        }

        Interlocked.Increment(ref _digipeatedFrames);
        logger.LogInformation("Digipeated {Tnc2}", rewritten.ToTnc2());
    }

    private static byte[] BuildDedupKey(Ax25Frame frame)
    {
        var header = System.Text.Encoding.ASCII.GetBytes($"{frame.Source}>{frame.Destination}:");
        var key = new byte[header.Length + frame.Info.Length];
        header.CopyTo(key, 0);
        frame.Info.CopyTo(key, header.Length);
        return key;
    }

    private async Task<HashSet<string>> GetOwnCallsignsAsync(DireControlContext db, CancellationToken ct)
    {
        if (DateTime.UtcNow - _ownCallsignsLoadedAt < OwnCallsignCacheTtl)
            return _ownCallsigns;

        var calls = await db.Radios.Select(r => r.FullCallsign).ToListAsync(ct);
        calls.Add(options.Value.OurCallsign);

        _ownCallsigns = new HashSet<string>(calls, StringComparer.OrdinalIgnoreCase);
        _ownCallsignsLoadedAt = DateTime.UtcNow;
        return _ownCallsigns;
    }
}
