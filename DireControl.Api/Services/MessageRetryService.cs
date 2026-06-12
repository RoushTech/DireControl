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

    private async Task ProcessDueRetriesAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();

        var due = await db.Messages
            .Where(m =>
                m.RetryState == RetryState.Retrying &&
                m.NextRetryAt <= DateTime.UtcNow &&
                m.RetryCount < m.MaxRetries)
            .ToListAsync(ct);

        if (due.Count == 0)
            return;

        logger.LogDebug("Processing {Count} due message retries.", due.Count);

        // Mutate all due messages, save once, then broadcast.
        var failed = new List<MessageFailedDto>();
        var retried = new List<MessageRetriedDto>();

        foreach (var message in due)
        {
            await messageSendingService.RetransmitAsync(message, ct);
            message.RetryCount++;
            message.LastSentAt = DateTime.UtcNow;

            if (message.RetryCount >= message.MaxRetries)
            {
                message.RetryState = RetryState.Failed;
                message.NextRetryAt = null;

                logger.LogInformation(
                    "Message {Id} to {ToCallsign} failed after {Count} attempts — no ACK received.",
                    message.Id, message.ToCallsign, message.RetryCount);

                failed.Add(new MessageFailedDto
                {
                    Id = message.Id,
                    ToCallsign = message.ToCallsign,
                    RetryCount = message.RetryCount,
                });
            }
            else
            {
                var delaySeconds = options.Value.InitialRetryDelaySeconds * Math.Pow(2, message.RetryCount - 1);
                message.NextRetryAt = DateTime.UtcNow.AddSeconds(delaySeconds);

                retried.Add(new MessageRetriedDto
                {
                    Id = message.Id,
                    RetryCount = message.RetryCount,
                    MaxRetries = message.MaxRetries,
                    NextRetryAt = message.NextRetryAt,
                    LastSentAt = message.LastSentAt,
                });
            }
        }

        await db.SaveChangesAsync(ct);

        foreach (var dto in failed)
            await hubContext.Clients.All.SendAsync(PacketHub.MessageFailedMethod, dto, ct);
        foreach (var dto in retried)
            await hubContext.Clients.All.SendAsync(PacketHub.MessageRetriedMethod, dto, ct);
    }
}
