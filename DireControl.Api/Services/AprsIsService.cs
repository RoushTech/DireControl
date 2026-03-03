using System.Threading.Channels;
using DireControl.Data;
using DireControl.Data.Models;
using DireControl.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using AprsConnectionState = AprsSharp.AprsIsClient.ConnectionState;

namespace DireControl.Api.Services;

/// <summary>
/// Long-running service that maintains a persistent connection to an
/// APRS-IS filtered server via <see cref="AprsSharp.AprsIsClient.AprsIsClient"/>,
/// reads incoming TNC2 packet lines, deduplicates them against recently
/// received RF packets, and feeds them into the existing packet processing pipeline.
/// </summary>
public sealed class AprsIsService(
    IServiceScopeFactory scopeFactory,
    IAprsIsStatusService statusService,
    AprsIsReconnectTrigger reconnectTrigger,
    IOptions<DireControlOptions> options,
    ILogger<AprsIsService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Create a per-iteration linked token so that either app shutdown
            // or a settings-change trigger can interrupt the connection.
            using var iterCts = CancellationTokenSource.CreateLinkedTokenSource(
                stoppingToken, reconnectTrigger.Token);
            var ct = iterCts.Token;

            try
            {
                var settings = await GetSettingsAsync(stoppingToken);

                if (!settings.AprsIsEnabled)
                {
                    statusService.SetState(AprsIsConnectionState.Disabled);
                    await Task.Delay(Timeout.Infinite, ct);
                    continue;
                }

                statusService.SetState(AprsIsConnectionState.Connecting);
                statusService.SetActiveFilter(settings.AprsIsFilter);
                statusService.ResetSessionCount();

                await ConnectAndReceiveAsync(settings, ct);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                // Settings changed or reconnect triggered — loop immediately to re-read settings.
                continue;
            }
            catch (AuthFailedException)
            {
                statusService.SetState(AprsIsConnectionState.AuthFailed);
                logger.LogError("APRS-IS passcode rejected. Fix the passcode in Settings, then save to reconnect.");
                // Wait indefinitely for a reconnect trigger (settings change) or app shutdown.
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (OperationCanceledException) { /* settings changed */ }
            }
            catch (Exception ex)
            {
                statusService.SetState(AprsIsConnectionState.Disconnected);
                logger.LogError(ex, "APRS-IS disconnected, reconnecting in 30 s");
                try { await Task.Delay(30_000, ct); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (OperationCanceledException) { /* reconnect trigger */ }
            }
        }

        statusService.SetState(AprsIsConnectionState.Disabled);
        logger.LogInformation("AprsIsService stopped.");
    }

    private async Task ConnectAndReceiveAsync(UserSetting settings, CancellationToken ct)
    {
        var passcode = settings.AprsIsPasscode
            ?? AprsPasscodeHelper.GeneratePasscode(options.Value.OurCallsign);

        logger.LogInformation("Connecting to APRS-IS at {Host}…", settings.AprsIsHost);

        var authFailed = false;
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        using var client = new AprsSharp.AprsIsClient.AprsIsClient();

        client.ReceivedTcpMessage += raw => channel.Writer.TryWrite(raw);

        client.ChangedState += state =>
        {
            // LoggedIn (successful auth) is handled in the channel consumer so we can
            // include the parsed server name from the login response line.
            var mapped = state switch
            {
                AprsConnectionState.Connected    => (AprsIsConnectionState?)AprsIsConnectionState.Connecting,
                AprsConnectionState.Disconnected => AprsIsConnectionState.Disconnected,
                _                                => null,
            };
            if (mapped is { } s)
                statusService.SetState(s);
        };

        client.DecodeFailed += (ex, packet) => logger.LogDebug(ex, "APRS-IS packet decode failed: {Packet}", packet);

        ct.Register(client.Disconnect);

        var receiveTask = client.Receive(
            options.Value.OurCallsign,
            passcode.ToString(),
            settings.AprsIsHost,
            settings.AprsIsFilter);

        // Complete the channel writer as soon as the receive loop ends so that
        // ReadAllAsync can drain and exit cleanly.
        _ = receiveTask.ContinueWith(_ => channel.Writer.TryComplete(), TaskScheduler.Default);

        try
        {
            await foreach (var raw in channel.Reader.ReadAllAsync(ct))
            {
                if (raw.StartsWith('#'))
                {
                    if (raw.Contains("unverified", StringComparison.OrdinalIgnoreCase))
                    {
                        authFailed = true;
                        client.Disconnect();
                    }
                    else if (raw.Contains("logresp", StringComparison.OrdinalIgnoreCase)
                          && raw.Contains("verified", StringComparison.OrdinalIgnoreCase))
                    {
                        // Parse server name: "# logresp W1AW verified, server T2USEAST"
                        string? serverName = null;
                        var serverIdx = raw.IndexOf("server ", StringComparison.OrdinalIgnoreCase);
                        if (serverIdx >= 0)
                            serverName = raw[(serverIdx + 7)..].Trim();
                        statusService.SetState(AprsIsConnectionState.Connected, serverName);
                    }
                    continue;
                }

                statusService.IncrementPacketCount();
                await ProcessLineAsync(raw, settings.DeduplicationWindowSeconds, ct);
            }
        }
        catch (OperationCanceledException)
        {
            client.Disconnect();
            try { await receiveTask; } catch { }
            throw;
        }

        await receiveTask;

        ct.ThrowIfCancellationRequested();

        if (authFailed)
            throw new AuthFailedException();

        throw new EndOfStreamException("APRS-IS connection closed.");
    }

    private async Task ProcessLineAsync(string tnc2, int dedupWindowSeconds, CancellationToken ct)
    {
        var callsign = ExtractCallsign(tnc2);
        if (string.IsNullOrWhiteSpace(callsign))
        {
            logger.LogTrace("Dropped APRS-IS line with no callsign: {Line}", tnc2);
            return;
        }

        var infoField = ExtractInfoField(tnc2);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();

        var dedupWindow = DateTime.UtcNow.AddSeconds(-dedupWindowSeconds);

        // Check for duplicate within window.
        var duplicate = await db.Packets
            .Where(p =>
                p.StationCallsign == callsign &&
                p.InfoField == infoField &&
                p.ReceivedAt >= dedupWindow)
            .OrderByDescending(p => p.ReceivedAt)
            .FirstOrDefaultAsync(ct);

        if (duplicate is not null)
        {
            // Already have this packet — discard APRS-IS copy (RF is authoritative).
            logger.LogTrace("Dedup: discarding APRS-IS copy of {Callsign} (existing id={Id}, source={Src})",
                callsign, duplicate.Id, duplicate.Source);
            return;
        }

        // New packet — create station if first sighting.
        var station = await db.Stations.FindAsync(new object[] { callsign }, ct);
        if (station is null)
        {
            db.Stations.Add(new Station
            {
                Callsign = callsign,
                FirstSeen = DateTime.UtcNow,
                LastSeen = DateTime.UtcNow,
                LastHeardAprsIs = DateTime.UtcNow,
                Symbol = "/-",
            });
        }
        else
        {
            station.LastSeen = DateTime.UtcNow;
            station.LastHeardAprsIs = DateTime.UtcNow;
        }

        db.Packets.Add(new Packet
        {
            StationCallsign = callsign,
            ReceivedAt = DateTime.UtcNow,
            RawPacket = tnc2,
            Source = PacketSource.AprsIs,
            InfoField = infoField,
        });

        await db.SaveChangesAsync(ct);

        logger.LogDebug("Stored APRS-IS packet from {Callsign}: {Line}", callsign, tnc2);
    }

    private async Task<UserSetting> GetSettingsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
        return await db.UserSettings.FindAsync(new object[] { 1 }, ct)
               ?? new UserSetting { Id = 1 };
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static string ExtractCallsign(string tnc2)
    {
        var idx = tnc2.IndexOf('>');
        return idx > 0 ? tnc2[..idx] : string.Empty;
    }

    private static string ExtractInfoField(string tnc2)
    {
        var idx = tnc2.IndexOf(':');
        return idx >= 0 ? tnc2[(idx + 1)..] : string.Empty;
    }

    // ─── Private exception ────────────────────────────────────────────────────

    private sealed class AuthFailedException : Exception
    {
        public AuthFailedException() : base("APRS-IS passcode rejected — check configuration") { }
    }
}
