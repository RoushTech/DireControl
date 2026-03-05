using System.Reflection;
using DireControl.Api.Services;
using DireControl.Data;
using DireControl.Data.Models;
using DireControl.Enums;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DireControl.Tests;

/// <summary>
/// Tests for APRS message formatting, ACK packet parsing, inbox deduplication,
/// and acknowledgement processing.  Uses an in-memory SQLite database so that
/// DB-level logic (dedup queries, ACK application) is exercised against real SQL
/// rather than an in-memory EF stub.
/// </summary>
public sealed class MessageHandlingTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DireControlContext _db;

    public MessageHandlingTests()
    {
        // Keep the connection open for the life of the test so the in-memory
        // SQLite database is not destroyed between context operations.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<DireControlContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new DireControlContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // =========================================================================
    // BuildMessageInfo — verifies correct APRS message packet formatting.
    // In particular, confirms no trailing '}' after the message number; that
    // trailing brace was the root cause of ACK round-trip failures.
    // =========================================================================

    [Theory]
    [InlineData("W1ABC", "Hello", "1", ":W1ABC    :Hello{1")]
    [InlineData("W1ABC-7", "Test msg", "42", ":W1ABC-7  :Test msg{42")]
    [InlineData("N0CALL", "hi", "99999", ":N0CALL   :hi{99999")]
    [InlineData("KB9VBR", "73 de me", "5", ":KB9VBR   :73 de me{5")]
    public void BuildMessageInfo_ProducesCorrectAprsFormat(
        string toCallsign, string body, string msgId, string expected)
    {
        var method = typeof(MessageSendingService)
            .GetMethod("BuildMessageInfo", BindingFlags.NonPublic | BindingFlags.Static)!;

        var result = (string)method.Invoke(null, [toCallsign, body, msgId])!;

        Assert.Equal(expected, result);
        // Belt-and-suspenders: ensure no trailing brace (the fixed bug).
        Assert.DoesNotContain('}', result);
    }

    // =========================================================================
    // BuildAckInfo — verifies ACK packet formatting.
    // =========================================================================

    [Theory]
    [InlineData("W1ABC", "1", ":W1ABC    :ack1")]
    [InlineData("W1ABC-7", "42", ":W1ABC-7  :ack42")]
    [InlineData("N0CALL", "3DF4A", ":N0CALL   :ack3DF4A")]
    public void BuildAckInfo_ProducesCorrectAprsFormat(
        string toCallsign, string msgId, string expected)
    {
        var method = typeof(MessageSendingService)
            .GetMethod("BuildAckInfo", BindingFlags.NonPublic | BindingFlags.Static)!;

        var result = (string)method.Invoke(null, [toCallsign, msgId])!;

        Assert.Equal(expected, result);
    }

    // =========================================================================
    // TryParseAck — body → (isAck, originalMsgId)
    // =========================================================================

    [Theory]
    [InlineData("ack42", true, "42")]
    [InlineData("ACK42", true, "42")]     // case-insensitive
    [InlineData("Ack42", true, "42")]     // mixed case
    [InlineData("ack3DF4A", true, "3DF4A")] // alphanumeric ID (real-world example)
    [InlineData("ack1", true, "1")]      // single-digit ID
    [InlineData("ack", false, "")]       // "ack" alone — no ID follows, too short
    [InlineData("Hello", false, "")]       // plain message body
    [InlineData("", false, "")]       // empty
    [InlineData("ack ", false, "")]       // "ack " — body.Length == 4 but ID is whitespace; trims to ""
    public void TryParseAck_ClassifiesBodyCorrectly(string body, bool expectedIsAck, string expectedId)
    {
        var isAck = MessageHandlingLogic.TryParseAck(body, out var id);

        Assert.Equal(expectedIsAck, isAck);
        Assert.Equal(expectedId, id);
    }

    // =========================================================================
    // IsMessageDuplicateAsync — dedup query
    // =========================================================================

    [Fact]
    public async Task IsMessageDuplicate_ReturnsFalse_WhenNoMatchingMessageExists()
    {
        var isDuplicate = await MessageHandlingLogic.IsMessageDuplicateAsync(
            "W1ABC", "42", _db, default);

        Assert.False(isDuplicate);
    }

    [Fact]
    public async Task IsMessageDuplicate_ReturnsTrue_WhenExactMatchExists()
    {
        SeedMessage(from: "W1ABC", to: "W3UWU", messageId: "42");
        await _db.SaveChangesAsync();

        var isDuplicate = await MessageHandlingLogic.IsMessageDuplicateAsync(
            "W1ABC", "42", _db, default);

        Assert.True(isDuplicate);
    }

    [Fact]
    public async Task IsMessageDuplicate_ReturnsFalse_WhenFromCallsignDiffers()
    {
        SeedMessage(from: "W1ABC", to: "W3UWU", messageId: "42");
        await _db.SaveChangesAsync();

        var isDuplicate = await MessageHandlingLogic.IsMessageDuplicateAsync(
            "W9XYZ", "42", _db, default);

        Assert.False(isDuplicate);
    }

    [Fact]
    public async Task IsMessageDuplicate_ReturnsFalse_WhenMessageIdDiffers()
    {
        SeedMessage(from: "W1ABC", to: "W3UWU", messageId: "42");
        await _db.SaveChangesAsync();

        var isDuplicate = await MessageHandlingLogic.IsMessageDuplicateAsync(
            "W1ABC", "99", _db, default);

        Assert.False(isDuplicate);
    }

    [Fact]
    public async Task IsMessageDuplicate_DoesNotMatchAcrossMultipleSenders()
    {
        SeedMessage(from: "W1ABC", to: "W3UWU", messageId: "1");
        SeedMessage(from: "W2DEF", to: "W3UWU", messageId: "2");
        await _db.SaveChangesAsync();

        // Each callsign's message ID is independent.
        Assert.False(await MessageHandlingLogic.IsMessageDuplicateAsync("W1ABC", "2", _db, default));
        Assert.False(await MessageHandlingLogic.IsMessageDuplicateAsync("W2DEF", "1", _db, default));
        Assert.True(await MessageHandlingLogic.IsMessageDuplicateAsync("W1ABC", "1", _db, default));
        Assert.True(await MessageHandlingLogic.IsMessageDuplicateAsync("W2DEF", "2", _db, default));
    }

    // =========================================================================
    // TryApplyAckAsync — marks sent message acknowledged
    // =========================================================================

    [Fact]
    public async Task TryApplyAck_MarksMessageAcknowledgedAndReturnsId()
    {
        SeedMessage(from: "W3UWU", to: "W1ABC", messageId: "7",
            retryState: RetryState.Retrying, nextRetryAt: DateTime.UtcNow.AddSeconds(30));
        await _db.SaveChangesAsync();

        var ackedId = await MessageHandlingLogic.TryApplyAckAsync(
            fromCallsign: "W1ABC",
            originalMsgId: "7",
            db: _db,
            ourCallsign: "W3UWU",
            ct: default);

        Assert.NotNull(ackedId);

        var msg = await _db.Messages.SingleAsync();
        Assert.True(msg.AckSent);
        Assert.Equal(RetryState.Acknowledged, msg.RetryState);
        Assert.Null(msg.NextRetryAt);
    }

    [Fact]
    public async Task TryApplyAck_ReturnedId_MatchesMessagePrimaryKey()
    {
        SeedMessage(from: "W3UWU", to: "W1ABC", messageId: "7",
            retryState: RetryState.Retrying);
        await _db.SaveChangesAsync();
        var expectedId = (await _db.Messages.SingleAsync()).Id;

        var ackedId = await MessageHandlingLogic.TryApplyAckAsync(
            "W1ABC", "7", _db, "W3UWU", default);

        Assert.Equal(expectedId, ackedId);
    }

    [Fact]
    public async Task TryApplyAck_IsCaseInsensitiveForBothCallsigns()
    {
        SeedMessage(from: "W3UWU", to: "W1ABC", messageId: "7",
            retryState: RetryState.Retrying);
        await _db.SaveChangesAsync();

        // Lowercase callsigns from the incoming packet should still match.
        var ackedId = await MessageHandlingLogic.TryApplyAckAsync(
            fromCallsign: "w1abc",
            originalMsgId: "7",
            db: _db,
            ourCallsign: "w3uwu",
            ct: default);

        Assert.NotNull(ackedId);
        Assert.True((await _db.Messages.SingleAsync()).AckSent);
    }

    [Fact]
    public async Task TryApplyAck_ReturnsNull_WhenNoMatchingSentMessage()
    {
        var ackedId = await MessageHandlingLogic.TryApplyAckAsync(
            "W1ABC", "999", _db, "W3UWU", default);

        Assert.Null(ackedId);
    }

    [Fact]
    public async Task TryApplyAck_ReturnsNull_WhenMessageAlreadyAcknowledged()
    {
        SeedMessage(from: "W3UWU", to: "W1ABC", messageId: "7",
            ackSent: true, retryState: RetryState.Acknowledged);
        await _db.SaveChangesAsync();

        var ackedId = await MessageHandlingLogic.TryApplyAckAsync(
            "W1ABC", "7", _db, "W3UWU", default);

        Assert.Null(ackedId);
    }

    [Fact]
    public async Task TryApplyAck_ReturnsNull_WhenMessageIdWrongButCallsignsMatch()
    {
        SeedMessage(from: "W3UWU", to: "W1ABC", messageId: "7",
            retryState: RetryState.Retrying);
        await _db.SaveChangesAsync();

        var ackedId = await MessageHandlingLogic.TryApplyAckAsync(
            "W1ABC", "8", _db, "W3UWU", default);

        Assert.Null(ackedId);
        Assert.False((await _db.Messages.SingleAsync()).AckSent);
    }

    [Fact]
    public async Task TryApplyAck_DoesNotAckMessageSentToWrongStation()
    {
        // Our message is addressed to W1ABC, but the ACK arrives from W9XYZ.
        SeedMessage(from: "W3UWU", to: "W1ABC", messageId: "7",
            retryState: RetryState.Retrying);
        await _db.SaveChangesAsync();

        var ackedId = await MessageHandlingLogic.TryApplyAckAsync(
            fromCallsign: "W9XYZ",
            originalMsgId: "7",
            db: _db,
            ourCallsign: "W3UWU",
            ct: default);

        Assert.Null(ackedId);
        Assert.False((await _db.Messages.SingleAsync()).AckSent);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private void SeedMessage(
        string from,
        string to,
        string messageId = "",
        string body = "Test",
        bool ackSent = false,
        RetryState retryState = RetryState.Pending,
        DateTime? nextRetryAt = null)
    {
        _db.Messages.Add(new Message
        {
            FromCallsign = from,
            ToCallsign = to,
            Body = body,
            MessageId = messageId,
            ReceivedAt = DateTime.UtcNow,
            IsRead = false,
            AckSent = ackSent,
            ReplySent = false,
            RetryState = retryState,
            MaxRetries = 5,
            NextRetryAt = nextRetryAt,
        });
    }
}
