using DireControl.Api.Controllers;
using DireControl.Api.Controllers.Models;
using DireControl.Api.Services;
using DireControl.Data;
using DireControl.Data.Models;
using DireControl.Enums;
using DireControl.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// Outbound message retries: multi-part messages go out one part at a time,
/// and a retry that never reached the air is not counted.
/// </summary>
[TestFixture]
public sealed class MessageRetryTests
{
    private static readonly IOptions<DireControlOptions> Options =
        Microsoft.Extensions.Options.Options.Create(new DireControlOptions { OurCallsign = IngestHarness.OurCallsign });

    private static MessageSendingService Sending(IngestHarness h) =>
        new(h.Transmitter, h.AprsIsTxQueue, h.ScopeFactory, Options, NullLogger<MessageSendingService>.Instance);

    private static MessageRetryService Retry(IngestHarness h, MessageSendingService sending) =>
        new(h.ScopeFactory, new NullHubContext(), sending, Options, NullLogger<MessageRetryService>.Instance);

    [Test]
    public async Task MultiPartMessage_SendsOnlyPartOneUntilItsAckArrives()
    {
        using var harness = new IngestHarness();
        var sending = Sending(harness);
        var retry = Retry(harness, sending);

        await using (var db = harness.CreateDb())
        {
            var controller = new MessagesController(db, Options, sending, new NullHubContext());
            await controller.Send(new SendMessageRequest
            {
                ToCallsign = "K4ABC",
                Body = string.Join(" ", Enumerable.Repeat("seventy-ish characters of text", 6)),
                Path = "WIDE1-1",
            }, default);
        }

        await using (var db = harness.CreateDb())
            Assert.That(await db.Messages.CountAsync(), Is.EqualTo(3), "three parts stored");
        Assert.That(harness.Transmitter.Sent, Has.Count.EqualTo(1), "only part 1 transmitted");

        await retry.ProcessDueRetriesAsync(default);
        Assert.That(harness.Transmitter.Sent, Has.Count.EqualTo(1), "part 2 waits while part 1 is in flight");

        await using (var db = harness.CreateDb())
        {
            var part1 = await db.Messages.OrderBy(m => m.Id).FirstAsync();
            await MessageHandlingLogic.TryApplyAckAsync("K4ABC", part1.MessageId, db, IngestHarness.OurCallsign, default);
        }

        await retry.ProcessDueRetriesAsync(default);
        Assert.That(harness.Transmitter.Sent, Has.Count.EqualTo(2), "part 2 follows part 1's ACK");
        await using (var db = harness.CreateDb())
        {
            var states = await db.Messages.OrderBy(m => m.Id).Select(m => m.RetryState).ToListAsync();
            Assert.That(states, Is.EqualTo(new[] { RetryState.Acknowledged, RetryState.Retrying, RetryState.Pending }));
        }
    }

    [Test]
    public async Task RetryThatCouldNotTransmit_DoesNotCountAsAnAttempt()
    {
        using var harness = new IngestHarness();
        harness.Transmitter.Accept = false;
        await using (var db = harness.CreateDb())
        {
            db.Messages.Add(new Message
            {
                FromCallsign = IngestHarness.OurCallsign, ToCallsign = "K4ABC", Body = "hi", MessageId = "7",
                ReceivedAt = DateTime.UtcNow, RetryState = RetryState.Retrying, RetryCount = 1, MaxRetries = 5,
                LastSentAt = DateTime.UtcNow.AddMinutes(-1), NextRetryAt = DateTime.UtcNow.AddSeconds(-1),
            });
            await db.SaveChangesAsync();
        }

        await Retry(harness, Sending(harness)).ProcessDueRetriesAsync(default);

        await using var check = harness.CreateDb();
        var msg = await check.Messages.SingleAsync();
        Assert.That(msg.RetryCount, Is.EqualTo(1));
        Assert.That(msg.RetryState, Is.EqualTo(RetryState.Retrying));
        Assert.That(msg.NextRetryAt, Is.GreaterThan(DateTime.UtcNow));
    }

    [Test]
    public async Task AckedMessage_IsNotOverwrittenByARetry()
    {
        // The due row was read, then the ACK landed before the retry wrote back.
        using var harness = new IngestHarness();
        await using (var db = harness.CreateDb())
        {
            db.Messages.Add(new Message
            {
                FromCallsign = IngestHarness.OurCallsign, ToCallsign = "K4ABC", Body = "hi", MessageId = "7",
                ReceivedAt = DateTime.UtcNow, RetryState = RetryState.Retrying, RetryCount = 4, MaxRetries = 5,
                LastSentAt = DateTime.UtcNow.AddMinutes(-1), NextRetryAt = DateTime.UtcNow.AddSeconds(-1),
            });
            await db.SaveChangesAsync();
        }
        harness.Transmitter.OnSend = () =>
        {
            using var db = harness.CreateDb();
            MessageHandlingLogic.TryApplyAckAsync("K4ABC", "7", db, IngestHarness.OurCallsign, default)
                .GetAwaiter().GetResult();
        };

        await Retry(harness, Sending(harness)).ProcessDueRetriesAsync(default);

        await using var check = harness.CreateDb();
        Assert.That((await check.Messages.SingleAsync()).RetryState, Is.EqualTo(RetryState.Acknowledged),
            "the final attempt must not turn an acknowledged message into Failed");
    }
}
