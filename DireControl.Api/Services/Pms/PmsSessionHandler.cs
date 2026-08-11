using System.Text;
using DireControl.Api.Services.Ax25;
using DireControl.Api.Services.Terminal;
using DireControl.Data;
using DireControl.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DireControl.Api.Services.Pms;

/// <summary>
/// Byte-stream adapter between an AX.25 session and the
/// <see cref="PmsCommandProcessor"/>: accumulates CR/LF/CRLF-terminated lines
/// (256-char cap), writes responses with CR endings (the packet convention),
/// and never echoes — the remote user's terminal does that.  Serves real
/// inbound RF sessions and the web terminal's local PMS preview identically.
/// </summary>
public sealed class PmsSessionHandler(
    IPmsMailStore store,
    IServiceScopeFactory scopeFactory,
    IOptions<DireControlOptions> options,
    ILogger<PmsSessionHandler> logger) : IPmsSessionServer
{
    private const int MaxLineLength = 256;

    public async Task HandleSessionAsync(IAx25Session session, CancellationToken ct)
    {
        var stationBase = StripSsid(options.Value.OurCallsign);
        var origin = session.Channel < 0 ? TerminalSessionOrigin.LocalPms : TerminalSessionOrigin.Inbound;
        var processor = new PmsCommandProcessor(
            store,
            session.Remote.ToString(),
            stationBase,
            () => DateTime.UtcNow,
            GetHeardListAsync,
            origin);

        try
        {
            var bannerText = await GetBannerTextAsync(ct);
            await WriteLinesAsync(session, await processor.GetBannerAsync(bannerText, ct), ct);
            await WritePromptAsync(session, processor, ct);

            var lineBuffer = new StringBuilder();
            var lastWasCr = false;

            await foreach (var data in session.Received.ReadAllAsync(ct))
            {
                foreach (var b in data)
                {
                    var c = (char)b;
                    if (c is '\r' or '\n')
                    {
                        // CRLF arrives as CR-then-LF: the LF completes the same line.
                        if (c == '\n' && lastWasCr)
                        {
                            lastWasCr = false;
                            continue;
                        }
                        lastWasCr = c == '\r';

                        var line = lineBuffer.ToString();
                        lineBuffer.Clear();
                        await WriteLinesAsync(session, await processor.ProcessLineAsync(line, ct), ct);
                        if (processor.DisconnectRequested)
                        {
                            await session.DisconnectAsync(ct);
                            return;
                        }
                        await WritePromptAsync(session, processor, ct);
                    }
                    else
                    {
                        lastWasCr = false;
                        if (lineBuffer.Length < MaxLineLength)
                            lineBuffer.Append(c);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown or session teardown — nothing to do.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "PMS session with {Remote} failed.", session.Remote);
        }
    }

    private static async ValueTask WriteLinesAsync(
        IAx25Session session, IReadOnlyList<string> lines, CancellationToken ct)
    {
        if (lines.Count == 0)
            return;
        var text = string.Concat(lines.Select(l => l + "\r"));
        await session.SendAsync(Encoding.ASCII.GetBytes(text), ct);
    }

    private static async ValueTask WritePromptAsync(
        IAx25Session session, PmsCommandProcessor processor, CancellationToken ct)
    {
        if (processor.AtCommandPrompt)
            await session.SendAsync(">\r"u8.ToArray(), ct);
    }

    private async Task<string> GetBannerTextAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
        var setting = await db.UserSettings.FindAsync([1], ct);
        return setting?.PmsBannerText ?? "Welcome.";
    }

    private async Task<IReadOnlyList<HeardStation>> GetHeardListAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
        return await db.Stations
            .AsNoTracking()
            .Where(s => s.LastHeardRf != null)
            .OrderByDescending(s => s.LastHeardRf)
            .Take(10)
            .Select(s => new HeardStation(s.Callsign, s.LastHeardRf!.Value))
            .ToListAsync(ct);
    }

    private static string StripSsid(string callsign)
    {
        var dash = callsign.IndexOf('-');
        return (dash > 0 ? callsign[..dash] : callsign).ToUpperInvariant();
    }
}
