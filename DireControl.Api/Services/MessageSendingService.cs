using DireControl.Data;
using DireControl.Data.Models;
using DireControl.Enums;
using Microsoft.Extensions.Options;

namespace DireControl.Api.Services;

/// <summary>
/// Formats APRS message packets, encodes them as AX.25/KISS frames, and
/// writes them to Direwolf via the shared <see cref="KissConnectionHolder"/>.
/// Outbound messages are stored in the <see cref="Message"/> table.
/// </summary>
public sealed class MessageSendingService(
    KissConnectionHolder connectionHolder,
    IServiceScopeFactory scopeFactory,
    IOptions<DireControlOptions> options,
    ILogger<MessageSendingService> logger)
{
    private static int _msgIdCounter;

    /// <summary>
    /// Generates a sequential numeric APRS message ID (1–99999); the counter resets on restart.
    /// </summary>
    public static string GenerateMessageId()
    {
        var n = Interlocked.Increment(ref _msgIdCounter) % 99999;
        return (n == 0 ? 1 : n).ToString();
    }

    /// <summary>
    /// Sends a message to <paramref name="toCallsign"/>, stores it in the
    /// database with <c>AckSent = false</c>, and returns the saved record.
    /// Returns <see langword="null"/> if no active Direwolf connection is available.
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
        var frame = Ax25Frame.BuildUiFrame(ourCallsign, info, path);

        if (!connectionHolder.TrySend(frame))
        {
            logger.LogWarning(
                "Cannot send message to {ToCallsign}: no active Direwolf connection.",
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
    /// Sends an ACK for a received message. ACKs are sent direct (no path).
    /// Does not store anything new in the database; the caller is responsible
    /// for setting <c>AckSent = true</c> on the corresponding
    /// <see cref="Message"/> record.
    /// </summary>
    public void SendAck(string toCallsign, string originalMessageId)
    {
        var ourCallsign = options.Value.OurCallsign.Trim().ToUpperInvariant();
        var info = BuildAckInfo(toCallsign, originalMessageId);
        var frame = Ax25Frame.BuildUiFrame(ourCallsign, info, path: string.Empty);

        if (!connectionHolder.TrySend(frame))
        {
            logger.LogWarning(
                "Cannot send ACK to {ToCallsign}: no active Direwolf connection.",
                toCallsign);
            return;
        }

        logger.LogDebug("Sent ACK to {ToCallsign} for message id={MessageId}.", toCallsign, originalMessageId);
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
        var frame = Ax25Frame.BuildUiFrame(ourCallsign, info, message.PathUsed ?? string.Empty);

        var sent = connectionHolder.TrySend(frame);
        if (!sent)
            logger.LogWarning(
                "Cannot retransmit message {Id} to {ToCallsign}: no active Direwolf connection.",
                message.Id, message.ToCallsign);

        if (sent)
            logger.LogInformation(
                "Retransmitted message {Id} to {ToCallsign} (id={MessageId}, path={Path}, attempt {Attempt}).",
                message.Id, message.ToCallsign, message.MessageId,
                string.IsNullOrEmpty(message.PathUsed) ? "(direct)" : message.PathUsed,
                message.RetryCount + 1);

        return Task.FromResult(sent);
    }

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
