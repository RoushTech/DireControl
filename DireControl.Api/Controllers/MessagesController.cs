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
    TxFrameRegistry txFrames,
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
                ReceiveCount = m.ReceiveCount,
                LastReceivedAt = m.LastReceivedAt,
                Source = m.Packet != null ? m.Packet.Source : null,
                HeardVia = m.Packet != null ? m.Packet.HeardVia : null,
                Path = m.Packet != null ? m.Packet.Path : null,
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
    public async Task<ActionResult<PaginatedResponse<AllMessagePacketDto>>> GetAllMessages(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? sender = null,
        [FromQuery] string? addressee = null,
        [FromQuery] string? text = null,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = db.Packets
            .AsNoTracking()
            .Where(p => p.ParsedType == PacketType.Message);

        // Filters run in SQL against plain columns. MessageData is a JSON-converted column
        // EF cannot see into, so the addressee and text come from the APRS message info
        // field ":ADDRESSEE:text" (addressee padded to 9 characters). Callsigns are stored
        // upper-case, so the input is normalised; free text uses LIKE, which SQLite matches
        // case-insensitively for ASCII.
        if (!string.IsNullOrWhiteSpace(sender))
        {
            var s = sender.Trim().ToUpperInvariant();
            query = query.Where(p => p.StationCallsign.Contains(s));
        }

        if (!string.IsNullOrWhiteSpace(addressee))
        {
            var a = addressee.Trim().ToUpperInvariant();
            query = query.Where(p => p.InfoField != null && p.InfoField.Substring(1, 9).Contains(a));
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            var pattern = "%" + EscapeLike(text.Trim()) + "%";
            query = query.Where(p => p.InfoField != null
                && EF.Functions.Like(p.InfoField.Substring(11), pattern, "\\"));
        }

        var totalCount = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(p => p.ReceivedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
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

        return Ok(new PaginatedResponse<AllMessagePacketDto>
        {
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            Items = items,
        });
    }

    [HttpPut("{id:int}/read")]
    public async Task<ActionResult<InboxMessageDto>> MarkRead(int id, CancellationToken ct)
    {
        var message = await db.Messages.Include(m => m.Packet).FirstOrDefaultAsync(m => m.Id == id, ct);
        if (message is null)
            return NotFound();

        if (!message.IsRead)
        {
            message.IsRead = true;
            await db.SaveChangesAsync(ct);
        }

        return Ok(ToInboxDto(message));
    }

    /// <summary>
    /// Every received copy of an inbound message, oldest first, with the route
    /// each took.  Retransmissions often arrive by different paths (direct,
    /// via a digipeater, or through APRS-IS).
    /// </summary>
    [HttpGet("{id:int}/copies")]
    public async Task<ActionResult<IReadOnlyList<MessageCopyDto>>> GetCopies(int id, CancellationToken ct)
    {
        var message = await db.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);
        if (message is null)
            return NotFound();

        var copies = await MessageHandlingLogic.FindCopiesAsync(message, db, ct);
        return Ok(copies.Select(p => new MessageCopyDto
        {
            PacketId = p.Id,
            ReceivedAt = p.ReceivedAt,
            Source = p.Source,
            HeardVia = p.HeardVia,
            Path = p.Path,
            HopCount = p.HopCount,
        }).ToList());
    }

    /// <summary>
    /// Sends a message, splitting text longer than 67 characters into numbered
    /// parts (see <see cref="MessageSplitter"/>).  Each part is its own APRS
    /// message with its own ID, ACK and retry schedule.  Returns every part sent,
    /// in order.
    /// </summary>
    [HttpPost("send")]
    public async Task<ActionResult<IReadOnlyList<InboxMessageDto>>> Send(
        [FromBody] SendMessageRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ToCallsign))
            return BadRequest("ToCallsign is required.");

        if (StationIdentityLogic.TransmitBlockedReason(options.Value.OurCallsign) is { } blocked)
            return Problem(statusCode: StatusCodes.Status409Conflict, detail: blocked);

        var parts = MessageSplitter.Split(request.Body ?? string.Empty);
        if (parts.Count == 0)
            return BadRequest("Body is required.");
        if (parts.Count > MessageSplitter.MaxParts)
            return BadRequest($"Message too long: {parts.Count} parts (max {MessageSplitter.MaxParts}).");

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

        var toCallsign = request.ToCallsign.Trim().ToUpperInvariant();
        var sent = new List<InboxMessageDto>(parts.Count);

        // Only the first part transmits now.  Later parts are stored held and
        // released one at a time by the retry service as each earlier part is
        // acknowledged or gives up — no multi-frame burst, no retries in step.
        var first = await messageSendingService.SendMessageAsync(
            toCallsign, parts[0], await messageSendingService.NextMessageIdAsync(db, ct), path, ct);
        if (first is null)
            return StatusCode(503, "No transmitter available (modem TX off or external TNC down).");
        // Queued, not sent: the part's lifecycle follows on txFrameStateChanged.
        sent.Add(ToInboxDto(first) with
        {
            TxFrameId = txFrames.LatestByRef(TxFrameKind.Message, first.MessageId)?.Id,
        });

        foreach (var part in parts.Skip(1))
        {
            var held = await messageSendingService.QueueHeldPartAsync(
                toCallsign, part, await messageSendingService.NextMessageIdAsync(db, ct), path, ct);
            sent.Add(ToInboxDto(held));
        }

        return Ok(sent);
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
            return StatusCode(503, "No transmitter available (modem TX off or external TNC down).");

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
                Id = msg.Id,
                RetryCount = msg.RetryCount,
                MaxRetries = msg.MaxRetries,
                NextRetryAt = msg.NextRetryAt,
                LastSentAt = msg.LastSentAt,
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

        msg.RetryCount = 0;
        msg.RetryState = RetryState.Retrying;
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

        msg.RetryState = RetryState.Cancelled;
        msg.NextRetryAt = null;
        await db.SaveChangesAsync(ct);

        return Ok(ToInboxDto(msg));
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    internal static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static InboxMessageDto ToInboxDto(Message m) => new()
    {
        Id = m.Id,
        FromCallsign = m.FromCallsign,
        ToCallsign = m.ToCallsign,
        Body = m.Body,
        MessageId = m.MessageId,
        PathUsed = m.PathUsed,
        ReceivedAt = m.ReceivedAt,
        ReceiveCount = m.ReceiveCount,
        LastReceivedAt = m.LastReceivedAt,
        Source = m.Packet?.Source,
        HeardVia = m.Packet?.HeardVia,
        Path = m.Packet?.Path,
        IsRead = m.IsRead,
        AckSent = m.AckSent,
        ReplySent = m.ReplySent,
        RetryCount = m.RetryCount,
        MaxRetries = m.MaxRetries,
        NextRetryAt = m.NextRetryAt,
        RetryState = m.RetryState,
        LastSentAt = m.LastSentAt,
    };
}
