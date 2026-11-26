using DireControl.Api.Controllers.Models;
using DireControl.Api.Hubs;
using DireControl.Data;
using DireControl.Enums;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DireControl.Api.Services;

/// <summary>
/// Background service that periodically retransmits unacknowledged outbound messages
/// using an exponential-backoff schedule. Broadcasts <c>messageRetried</c>,
/// <c>messageFailed</c> SignalR events so the UI can update in real time.
/// </summary>
public sealed class MessageRetryService(
    IServiceScopeFactory scopeFactory,
    IHubContext<PacketHub> hubContext,
    MessageSendingService messageSendingService,
    IOptions<DireControlOptions> options,
    ILogger<MessageRetryService> logger) : BackgroundService
{
    private const int PollIntervalMs = 10_000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessDueRetriesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error in MessageRetryService.");
            }

            await Task.Delay(PollIntervalMs, stoppingToken);
        }

        logger.LogInformation("MessageRetryService stopped.");
    }

    /// <summary>One retry pass: release held multi-part parts, then retransmit what is due.</summary>
    internal async Task ProcessDueRetriesAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();

        await ReleaseHeldPartsAsync(db, ct);

        var due = await db.Messages
            .AsNoTracking()
            .Where(m =>
                m.RetryState == RetryState.Retrying &&
                m.NextRetryAt <= DateTime.UtcNow &&
                m.RetryCount < m.MaxRetries)
            .ToListAsync(ct);

        if (due.Count == 0)
            return;

        logger.LogDebug("Processing {Count} due message retries.", due.Count);

        foreach (var message in due)
        {
            if (!await messageSendingService.RetransmitAsync(message, ct))
            {
                // Nothing went to air: don't spend an attempt on it.  Check
                // again after the base delay rather than every poll.
                await db.Messages
                    .Where(m => m.Id == message.Id && m.RetryState == RetryState.Retrying && !m.AckSent)
                    .ExecuteUpdateAsync(s => s.SetProperty(
                        m => m.NextRetryAt, DateTime.UtcNow.AddSeconds(options.Value.InitialRetryDelaySeconds)), ct);
                continue;
            }

            var retryCount = message.RetryCount + 1;
            var sentAt = DateTime.UtcNow;
            var failed = retryCount >= message.MaxRetries;
            DateTime? nextRetryAt = failed
                ? null
                : sentAt.AddSeconds(options.Value.InitialRetryDelaySeconds * Math.Pow(2, retryCount - 1));

            // Conditional: an ACK may have landed while we were transmitting,
            // and must never be overwritten with Retrying or Failed.
            var updated = await db.Messages
                .Where(m => m.Id == message.Id && m.RetryState == RetryState.Retrying && !m.AckSent)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.RetryCount, retryCount)
                    .SetProperty(m => m.LastSentAt, sentAt)
                    .SetProperty(m => m.NextRetryAt, nextRetryAt)
                    .SetProperty(m => m.RetryState, failed ? RetryState.Failed : RetryState.Retrying), ct);
            if (updated == 0)
                continue;

            if (failed)
            {
                logger.LogInformation(
                    "Message {Id} to {ToCallsign} failed after {Count} attempts: no ACK received.",
                    message.Id, message.ToCallsign, retryCount);

                await hubContext.Clients.All.SendAsync(
                    PacketHub.MessageFailedMethod,
                    new MessageFailedDto
                    {
                        Id = message.Id,
                        ToCallsign = message.ToCallsign,
                        RetryCount = retryCount,
                    },
                    ct);
            }
            else
            {
                await hubContext.Clients.All.SendAsync(
                    PacketHub.MessageRetriedMethod,
                    new MessageRetriedDto
                    {
                        Id = message.Id,
                        RetryCount = retryCount,
                        MaxRetries = message.MaxRetries,
                        NextRetryAt = nextRetryAt,
                        LastSentAt = sentAt,
                    },
                    ct);
            }
        }
    }

    /// <summary>
    /// Sends the next held part of each multi-part message once nothing earlier
    /// to that peer is still in flight (Retrying) — part i+1 follows part i's
    /// ACK or give-up, never alongside it.
    /// </summary>
    private async Task ReleaseHeldPartsAsync(DireControlContext db, CancellationToken ct)
    {
        var ourCallsign = options.Value.OurCallsign.Trim().ToUpperInvariant();
        var held = await db.Messages
            .Where(m => m.FromCallsign == ourCallsign
                     && m.RetryState == RetryState.Pending
                     && m.LastSentAt == null
                     && !m.AckSent)
            .OrderBy(m => m.Id)
            .ToListAsync(ct);

        foreach (var next in held.GroupBy(m => m.ToCallsign).Select(g => g.First()))
        {
            var blocked = await db.Messages.AnyAsync(
                m => m.FromCallsign == ourCallsign
                  && m.ToCallsign == next.ToCallsign
                  && m.Id < next.Id
                  && m.RetryState == RetryState.Retrying,
                ct);
            if (blocked || !await messageSendingService.RetransmitAsync(next, ct))
                continue;

            next.RetryState = RetryState.Retrying;
            next.LastSentAt = DateTime.UtcNow;
            next.NextRetryAt = DateTime.UtcNow.AddSeconds(options.Value.InitialRetryDelaySeconds);
            await db.SaveChangesAsync(ct);

            await hubContext.Clients.All.SendAsync(
                PacketHub.MessageRetriedMethod,
                new MessageRetriedDto
                {
                    Id = next.Id,
                    RetryCount = next.RetryCount,
                    MaxRetries = next.MaxRetries,
                    NextRetryAt = next.NextRetryAt,
                    LastSentAt = next.LastSentAt,
                },
                ct);
        }
    }
}
