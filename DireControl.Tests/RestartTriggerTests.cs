using DireControl.Api.Services;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// The restart/reconnect triggers are read from service loops while settings
/// saves fire them; a read racing a trigger must never throw.
/// </summary>
[TestFixture]
public sealed class RestartTriggerTests
{
    [Test]
    public void TokenReads_RacingTriggers_NeverThrow()
    {
        var trigger = new RestartSignal();
        Exception? failure = null;
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        var reader = Task.Run(() =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                    _ = trigger.Token.IsCancellationRequested;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        while (!stop.IsCancellationRequested)
            trigger.Trigger();
        reader.Wait();

        Assert.That(failure, Is.Null);
    }

    [Test]
    public void Trigger_CancelsTheCurrentTokenAndIssuesAFreshOne()
    {
        var trigger = new RestartSignal();
        var before = trigger.Token;

        trigger.Trigger();

        Assert.That(before.IsCancellationRequested, Is.True);
        Assert.That(trigger.Token.IsCancellationRequested, Is.False);
    }
}
