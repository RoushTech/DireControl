using DireControl.Api.Controllers.Models;
using DireControl.Api.Hubs;
using DireControl.Api.Services;
using DireControl.Data;
using DireControl.Data.Models;
using DireControl.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DireControl.Api.Controllers;

[ApiController]
[Route("api/v0/messages")]
public class MessagesController(
    DireControlContext db,
    IOptions<DireControlOptions> options,
    MessageSendingService messageSendingService,
    IHubContext<PacketHub> hubContext) : ControllerBase
{
    [HttpGet("inbox")]
    public async Task<ActionResult<IReadOnlyList<InboxMessageDto>>> GetInbox(CancellationToken ct)
    {
        var ourCallsign = options.Value.OurCallsign.Trim();
        if (string.IsNullOrWhiteSpace(ourCallsign))
            return Ok(Array.Empty<InboxMessageDto>());

        var messages = await db.Messages
            .AsNoTracking()
            .Where(m => m.ToCallsign == ourCallsign || m.FromCallsign == ourCallsign)
            .OrderByDescending(m => m.ReceivedAt)
            .Select(m => new InboxMessageDto
            {
                Id = m.Id,
                FromCallsign = m.FromCallsign,
                ToCallsign = m.ToCallsign,
                Body = m.Body,
                MessageId = m.MessageId,
                PathUsed = m.PathUsed,
                ReceivedAt = m.ReceivedAt,
                IsRead = m.IsRead,
                AckSent = m.AckSent,
                ReplySent = m.ReplySent,
                RetryCount = m.RetryCount,
                MaxRetries = m.MaxRetries,
                NextRetryAt = m.NextRetryAt,
                RetryState = m.RetryState,
                LastSentAt = m.LastSentAt,
            })
            .ToListAsync(ct);

        return Ok(messages);
    }

    [HttpGet("all")]
    public async Task<ActionResult<IReadOnlyList<AllMessagePacketDto>>> GetAllMessages(CancellationToken ct)
    {
        var messages = await db.Packets
            .AsNoTracking()
            .Where(p => p.ParsedType == PacketType.Message)
            .OrderByDescending(p => p.ReceivedAt)
            .Select(p => new AllMessagePacketDto
            {
                PacketId = p.Id,
                FromCallsign = p.StationCallsign,
                ToCallsign = p.MessageData != null ? p.MessageData.Addressee : string.Empty,
                Body = p.MessageData != null ? p.MessageData.Text : string.Empty,
                MessageId = p.MessageData != null ? p.MessageData.MessageId : null,
                ReceivedAt = p.ReceivedAt,
                RawPacket = p.RawPacket,
            })
            .ToListAsync(ct);

        return Ok(messages);
    }

    [HttpPut("{id:int}/read")]
    public async Task<ActionResult<InboxMessageDto>> MarkRead(int id, CancellationToken ct)
    {
        var message = await db.Messages.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (message is null)
            return NotFound();

        if (!message.IsRead)
        {
            message.IsRead = true;
            await db.SaveChangesAsync(ct);
        }

        return Ok(ToInboxDto(message));
    }

    [HttpPost("send")]
    public async Task<ActionResult<InboxMessageDto>> Send(
        [FromBody] SendMessageRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ToCallsign))
            return BadRequest("ToCallsign is required.");

        var body = request.Body?.Trim() ?? string.Empty;
        if (body.Length > 67)
            body = body[..67];

        // Resolve path: per-message override takes precedence over the stored default.
        string path;
        if (!string.IsNullOrWhiteSpace(request.Path))
        {
            path = request.Path.Trim();
        }
        else
        {
            var userSetting = await db.UserSettings.FindAsync([1], ct);
            path = userSetting?.OutboundPath ?? "WIDE1-1,WIDE2-1";
        }

        var messageId = MessageSendingService.GenerateMessageId();
        var message = await messageSendingService.SendMessageAsync(
            request.ToCallsign.Trim().ToUpperInvariant(),
            body,
            messageId,
            path,
            ct);

        if (message is null)
            return StatusCode(503, "No active Direwolf connection.");

        return Ok(ToInboxDto(message));
    }

    /// <summary>
    /// Immediately retransmits the message and resets the retry schedule from
    /// the current attempt count. If the message is in Failed or Cancelled state,
    /// RetryCount is first reset to 0 to restart the full schedule.
    /// </summary>
    [HttpPost("{id:int}/retry")]
    public async Task<ActionResult<InboxMessageDto>> Retry(int id, CancellationToken ct)
    {
        var msg = await db.Messages.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (msg is null)
            return NotFound();

        if (msg.RetryState is RetryState.Failed or RetryState.Cancelled)
            msg.RetryCount = 0;

        var sent = await messageSendingService.RetransmitAsync(msg, ct);
        if (!sent)
            return StatusCode(503, "No active Direwolf connection.");

        msg.RetryCount++;
        msg.LastSentAt = DateTime.UtcNow;

        if (msg.RetryCount >= msg.MaxRetries)
        {
            msg.RetryState = RetryState.Failed;
            msg.NextRetryAt = null;
        }
        else
        {
            msg.RetryState = RetryState.Retrying;
            var delaySeconds = options.Value.InitialRetryDelaySeconds * Math.Pow(2, msg.RetryCount - 1);
            msg.NextRetryAt = DateTime.UtcNow.AddSeconds(delaySeconds);
        }

        await db.SaveChangesAsync(ct);

        await hubContext.Clients.All.SendAsync(
            PacketHub.MessageRetriedMethod,
            new MessageRetriedDto
            {
                Id          = msg.Id,
                RetryCount  = msg.RetryCount,
                MaxRetries  = msg.MaxRetries,
                NextRetryAt = msg.NextRetryAt,
                LastSentAt  = msg.LastSentAt,
            },
            ct);

        return Ok(ToInboxDto(msg));
    }

    /// <summary>
    /// Resets RetryCount to 0, sets RetryState to Retrying, and schedules the
    /// next retry <see cref="DireControlOptions.InitialRetryDelaySeconds"/> seconds from now.
    /// </summary>
    [HttpPost("{id:int}/reset")]
    public async Task<ActionResult<InboxMessageDto>> Reset(int id, CancellationToken ct)
    {
        var msg = await db.Messages.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (msg is null)
            return NotFound();

        msg.RetryCount  = 0;
        msg.RetryState  = RetryState.Retrying;
        msg.NextRetryAt = DateTime.UtcNow.AddSeconds(options.Value.InitialRetryDelaySeconds);
        await db.SaveChangesAsync(ct);

        return Ok(ToInboxDto(msg));
    }

    /// <summary>
    /// Sets RetryState to Cancelled and clears NextRetryAt.
    /// No further automatic retries will fire for this message.
    /// </summary>
    [HttpPost("{id:int}/cancel")]
    public async Task<ActionResult<InboxMessageDto>> Cancel(int id, CancellationToken ct)
    {
        var msg = await db.Messages.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (msg is null)
            return NotFound();

        msg.RetryState  = RetryState.Cancelled;
        msg.NextRetryAt = null;
        await db.SaveChangesAsync(ct);

        return Ok(ToInboxDto(msg));
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static InboxMessageDto ToInboxDto(Message m) => new()
    {
        Id          = m.Id,
        FromCallsign = m.FromCallsign,
        ToCallsign   = m.ToCallsign,
        Body        = m.Body,
        MessageId   = m.MessageId,
        PathUsed    = m.PathUsed,
        ReceivedAt  = m.ReceivedAt,
        IsRead      = m.IsRead,
        AckSent     = m.AckSent,
        ReplySent   = m.ReplySent,
        RetryCount  = m.RetryCount,
        MaxRetries  = m.MaxRetries,
        NextRetryAt = m.NextRetryAt,
        RetryState  = m.RetryState,
        LastSentAt  = m.LastSentAt,
    };
}
