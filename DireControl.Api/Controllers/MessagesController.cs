using DireControl.Api.Contracts;
using DireControl.Api.Services;
using DireControl.Data;
using DireControl.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DireControl.Api.Controllers;

[ApiController]
[Route("api/messages")]
public class MessagesController(
    DireControlContext db,
    IOptions<DireControlOptions> options,
    MessageSendingService messageSendingService) : ControllerBase
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
                ReceivedAt = m.ReceivedAt,
                IsRead = m.IsRead,
                AckSent = m.AckSent,
                ReplySent = m.ReplySent,
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

        return Ok(new InboxMessageDto
        {
            Id = message.Id,
            FromCallsign = message.FromCallsign,
            ToCallsign = message.ToCallsign,
            Body = message.Body,
            MessageId = message.MessageId,
            ReceivedAt = message.ReceivedAt,
            IsRead = message.IsRead,
            AckSent = message.AckSent,
            ReplySent = message.ReplySent,
        });
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

        var messageId = MessageSendingService.GenerateMessageId();
        var message = await messageSendingService.SendMessageAsync(
            request.ToCallsign.Trim().ToUpperInvariant(),
            body,
            messageId,
            ct);

        if (message is null)
            return StatusCode(503, "No active Direwolf connection.");

        return Ok(new InboxMessageDto
        {
            Id = message.Id,
            FromCallsign = message.FromCallsign,
            ToCallsign = message.ToCallsign,
            Body = message.Body,
            MessageId = message.MessageId,
            ReceivedAt = message.ReceivedAt,
            IsRead = message.IsRead,
            AckSent = message.AckSent,
            ReplySent = message.ReplySent,
        });
    }
}
