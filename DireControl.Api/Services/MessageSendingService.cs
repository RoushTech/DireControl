using DireControl.Data;
using DireControl.Data.Models;
using DireControl.Enums;
using DireControl.Modem.Ax25;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DireControl.Api.Services;

/// <summary>
/// Formats APRS message packets, encodes them as AX.25 frames, and sends
/// them over RF via the shared <see cref="IFrameTransmitter"/>.
/// Outbound messages are stored in the <see cref="Message"/> table.
/// </summary>
public sealed class MessageSendingService(
    IFrameTransmitter transmitter,
    AprsIsTxQueue aprsIsTxQueue,
    IServiceScopeFactory scopeFactory,
    IOptions<DireControlOptions> options,
    ILogger<MessageSendingService> logger)
{
    private readonly SemaphoreSlim _idSeedLock = new(1, 1);
    private int _lastMsgNumber = -1;

    /// <summary>
    /// Generates the next 1–5 character APRS message ID.  The sequence resumes
    /// from the newest outbound message in the database on first use, so a
    /// restart never reissues IDs that peers may still be deduplicating on.
    /// </summary>
    public async Task<string> NextMessageIdAsync(DireControlContext db, CancellationToken ct = default)
    {
        if (Volatile.Read(ref _lastMsgNumber) < 0)
        {
            await _idSeedLock.WaitAsync(ct);
            try
            {
                if (_lastMsgNumber < 0)
                {
                    var lastId = await MessageHandlingLogic.FindLastOutboundMessageIdAsync(
                        db, options.Value.OurCallsign, ct);
                    Volatile.Write(ref _lastMsgNumber, MessageHandlingLogic.NextMessageNumber(lastId) - 1);
                }
            }
            finally
            {
                _idSeedLock.Release();
            }
        }

        int last, next;
        do
        {
            last = Volatile.Read(ref _lastMsgNumber);
            next = MessageHandlingLogic.NextMessageNumber(last.ToString());
        }
        while (Interlocked.CompareExchange(ref _lastMsgNumber, next, last) != last);
        return next.ToString();
    }

    /// <summary>
    /// Sends a message to <paramref name="toCallsign"/>, stores it in the
    /// database with <c>AckSent = false</c>, and returns the saved record.
    /// Returns <see langword="null"/> if no RF transmit backend is available.
    /// </summary>
    /// <param name="path">
    /// VIA digipeater path (e.g. "WIDE1-1,WIDE2-1"). Pass an empty string to
    /// send direct with no digipeating.
    /// </param>
    public async Task<Message?> SendMessageAsync(
        string toCallsign,
        string body,
        string messageId,
        string path,
        CancellationToken ct = default)
    {
        var ourCallsign = options.Value.OurCallsign.Trim().ToUpperInvariant();
        var info = BuildMessageInfo(toCallsign, body, messageId);
        var frame = Ax25Encoder.EncodeUiFrame(ourCallsign, info, path);

        if (transmitter.Send(frame, 0, new TxFrameInfo(
                TxFrameKind.Message, $"Message to {toCallsign.Trim().ToUpperInvariant()}", messageId)) is null)
        {
            logger.LogWarning(
                "Cannot send message to {ToCallsign}: no RF transmit backend available.",
                toCallsign);
            return null;
        }

        logger.LogInformation(
            "Sent APRS message to {ToCallsign} (id={MessageId}, path={Path}): {Body}",
            toCallsign, messageId,
            string.IsNullOrEmpty(path) ? "(direct)" : path,
            body);

        using var storeScope = scopeFactory.CreateScope();
        var storeDb = storeScope.ServiceProvider.GetRequiredService<DireControlContext>();

        var message = new Message
        {
            FromCallsign = ourCallsign,
            ToCallsign = toCallsign.Trim().ToUpperInvariant(),
            Body = body,
            MessageId = messageId,
            PathUsed = string.IsNullOrWhiteSpace(path) ? null : path.Trim(),
            ReceivedAt = DateTime.UtcNow,
            IsRead = true,
            AckSent = false,
            ReplySent = false,
            RetryCount = 0,
            MaxRetries = options.Value.MaxRetryAttempts,
            RetryState = RetryState.Retrying,
            LastSentAt = DateTime.UtcNow,
            NextRetryAt = DateTime.UtcNow.AddSeconds(options.Value.InitialRetryDelaySeconds),
        };

        storeDb.Messages.Add(message);
        await storeDb.SaveChangesAsync(ct);
        return message;
    }

    /// <summary>
    /// Stores a later part of a multi-part message without transmitting it.
    /// Held parts are Pending with no <see cref="Message.LastSentAt"/>;
    /// <see cref="MessageRetryService"/> sends each one only once every earlier
    /// message to the same peer has been acknowledged or has failed, so the
    /// parts go out — and retry — one at a time instead of as a burst.
    /// </summary>
    public async Task<Message> QueueHeldPartAsync(
        string toCallsign,
        string body,
        string messageId,
        string path,
        CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();

        var message = new Message
        {
            FromCallsign = options.Value.OurCallsign.Trim().ToUpperInvariant(),
            ToCallsign = toCallsign.Trim().ToUpperInvariant(),
            Body = body,
            MessageId = messageId,
            PathUsed = string.IsNullOrWhiteSpace(path) ? null : path.Trim(),
            ReceivedAt = DateTime.UtcNow,
            IsRead = true,
            MaxRetries = options.Value.MaxRetryAttempts,
            RetryState = RetryState.Pending,
            LastSentAt = null,
            NextRetryAt = null,
        };

        db.Messages.Add(message);
        await db.SaveChangesAsync(ct);
        return message;
    }

    /// <summary>
    /// Sends an ACK for a received message back the way the sender can hear it.
    /// RF ACKs use the outbound digipeater path: a sender we heard through a
    /// digipeater is usually out of direct range, and a digipeated ACK also
    /// reaches iGates and therefore APRS-IS.  A message that arrived over
    /// APRS-IS is also ACKed directly on APRS-IS, because the sender may not be
    /// on our RF frequency at all; RF is added only when the sender was heard
    /// on RF recently (the standard IS→RF rule).
    /// Does not store anything; the caller sets <c>AckSent</c>.
    /// </summary>
    public async Task SendAckAsync(
        string toCallsign,
        string originalMessageId,
        PacketSource arrivedVia,
        CancellationToken ct = default)
    {
        var ourCallsign = options.Value.OurCallsign.Trim().ToUpperInvariant();
        var info = BuildAckInfo(toCallsign, originalMessageId);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
        var settings = await db.UserSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct)
                       ?? new UserSetting { Id = 1 };

        var sendRf = true;
        if (arrivedVia == PacketSource.AprsIs)
        {
            if (settings.AprsIsEnabled)
            {
                aprsIsTxQueue.Enqueue(IgateLogic.BuildIsOriginatedLine(ourCallsign, info));
                logger.LogDebug("Queued APRS-IS ACK to {ToCallsign} for message id={MessageId}.",
                    toCallsign, originalMessageId);

                var heardCutoff = DateTime.UtcNow.AddMinutes(-Math.Max(1, settings.IsToRfRecentHeardMinutes));
                var peer = toCallsign.Trim().ToUpperInvariant();
                sendRf = await db.Stations.AnyAsync(
                    s => s.Callsign == peer && s.LastHeardRf != null && s.LastHeardRf >= heardCutoff,
                    ct);
            }
        }

        if (!sendRf)
            return;

        var frame = Ax25Encoder.EncodeUiFrame(ourCallsign, info, settings.OutboundPath ?? string.Empty);
        if (transmitter.Send(frame, 0, new TxFrameInfo(
                TxFrameKind.Ack, $"ACK to {toCallsign.Trim().ToUpperInvariant()}", originalMessageId)) is null)
        {
            logger.LogWarning(
                "Cannot send RF ACK to {ToCallsign}: no RF transmit backend available.",
                toCallsign);
            return;
        }

        logger.LogDebug("Sent RF ACK to {ToCallsign} for message id={MessageId} via {Path}.",
            toCallsign, originalMessageId,
            string.IsNullOrEmpty(settings.OutboundPath) ? "(direct)" : settings.OutboundPath);
    }

    /// <summary>
    /// Retransmits an existing message from the database, reusing the path
    /// stored on the record so that retries go out the same way as the original.
    /// Returns <see langword="true"/> if sent successfully,
    /// <see langword="false"/> if there is no active connection.
    /// </summary>
    public Task<bool> RetransmitAsync(Message message, CancellationToken ct = default)
    {
        var ourCallsign = options.Value.OurCallsign.Trim().ToUpperInvariant();
        var info = BuildMessageInfo(message.ToCallsign, message.Body, message.MessageId);
        var frame = Ax25Encoder.EncodeUiFrame(ourCallsign, info, message.PathUsed ?? string.Empty);

        var sent = transmitter.Send(frame, 0, new TxFrameInfo(
            TxFrameKind.Message, $"Message to {message.ToCallsign} (retry)", message.MessageId)) is not null;
        if (!sent)
            logger.LogWarning(
                "Cannot retransmit message {Id} to {ToCallsign}: no RF transmit backend available.",
                message.Id, message.ToCallsign);

        if (sent)
            logger.LogInformation(
                "Retransmitted message {Id} to {ToCallsign} (id={MessageId}, path={Path}, attempt {Attempt}).",
                message.Id, message.ToCallsign, message.MessageId,
                string.IsNullOrEmpty(message.PathUsed) ? "(direct)" : message.PathUsed,
                message.RetryCount + 1);

        return Task.FromResult(sent);
    }

    // -------------------------------------------------------------------------
    // APRS packet formatting
    // -------------------------------------------------------------------------

    /// <summary>
    /// Formats the APRS info field for a message:
    /// <c>:ADDRESSEE :body{msgid</c> (no closing brace — per APRS spec).
    /// </summary>
    private static string BuildMessageInfo(string toCallsign, string body, string messageId)
    {
        // Addressee field is exactly 9 characters, right-padded with spaces.
        var addressee = toCallsign.ToUpperInvariant().PadRight(9)[..9];
        // APRS message-number format is {MSGNO with NO closing brace (APRS101 §14).
        // A trailing } is non-standard and causes some clients to include it in the
        // message ID they echo back in the ACK, breaking the MessageId lookup.
        return $":{addressee}:{body}{{{messageId}";
    }

    /// <summary>
    /// Formats the APRS info field for an ACK:
    /// <c>:ADDRESSEE :ackMSGID</c>
    /// </summary>
    private static string BuildAckInfo(string toCallsign, string originalMessageId)
    {
        var addressee = toCallsign.ToUpperInvariant().PadRight(9)[..9];
        return $":{addressee}:ack{originalMessageId}";
    }
}
