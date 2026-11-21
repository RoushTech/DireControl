using DireControl.Api.Services;
using DireControl.Modem.Ax25;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// <see cref="FrameTransmitService.TrySend"/> is the one choke point every
/// producer passes through, so its size and validity cap is what stops one
/// LAN client from keying the radio for minutes.
/// </summary>
[TestFixture]
public sealed class FrameTransmitServiceTests
{
    private static FrameTransmitService Create() =>
        new(new ServiceCollection().BuildServiceProvider(), new KissConnectionHolder(),
            NullLogger<FrameTransmitService>.Instance);

    [Test]
    public void TrySend_4KbPayload_ReturnsFalseAndCancelsCompletion()
    {
        var header = Ax25Encoder.EncodeUiFrame("N4WR-1", "x", null);
        var frame = header.Concat(new byte[4096]).ToArray();
        var completion = new TaskCompletionSource<bool>();

        var sent = Create().TrySend(frame, 0, TxPriority.Normal, completion);

        Assert.That(sent, Is.False);
        Assert.That(completion.Task.IsCanceled, Is.True);
    }

    [Test]
    public void TrySend_GarbageBytes_ReturnsFalse()
    {
        Assert.That(Create().TrySend([0x01, 0x02, 0x03]), Is.False);
    }

    [Test]
    public void IsTransmittable_MaximumAprsFrame_IsAccepted()
    {
        var frame = Ax25Encoder.EncodeUiFrame(
            "N4WR-15", new string('x', 256), "WIDE1-1,WIDE2-2,K4ABC-1,K4ABC-2,K4ABC-3,K4ABC-4,K4ABC-5,K4ABC-6");

        Assert.That(frame.Length, Is.LessThanOrEqualTo(FrameTransmitService.MaxFrameBytes));
        Assert.That(FrameTransmitService.IsTransmittable(frame), Is.True);
    }
}
