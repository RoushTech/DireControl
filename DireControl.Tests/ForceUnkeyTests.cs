using DireControl.Api.Services;
using DireControl.Enums;
using DireControl.Modem.Ptt;
using NUnit.Framework;

namespace DireControl.Tests;

[TestFixture]
public sealed class ForceUnkeyTests
{
    private sealed class RecordingPtt(bool throwOnSet = false) : IPttController
    {
        public List<bool> Calls { get; } = [];
        public bool Disposed { get; private set; }

        public void SetPtt(bool transmit)
        {
            if (throwOnSet)
                throw new IOException("device gone");
            Calls.Add(transmit);
        }

        public void Dispose() => Disposed = true;
    }

    [TestCase(ModemState.Error)]
    [TestCase(ModemState.Disabled)]
    public void FaultedSession_OpensFreshController_ReleasesAndDisposes(ModemState state)
    {
        var ptt = new RecordingPtt();
        var result = SoundModemService.TryForceUnkey(state, () => ptt, out var error);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo(ForceUnkeyResult.Released));
            Assert.That(error, Is.Null);
            Assert.That(ptt.Calls, Is.EqualTo(new[] { false }), "only ever unkeys");
            Assert.That(ptt.Disposed, Is.True);
        });
    }

    [Test]
    public void RunningSession_IsRefused_WithoutTouchingPtt()
    {
        var created = false;
        var result = SoundModemService.TryForceUnkey(
            ModemState.Running, () => { created = true; return new RecordingPtt(); }, out _);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo(ForceUnkeyResult.SessionRunning));
            Assert.That(created, Is.False, "the running session owns the PTT device");
        });
    }

    [Test]
    public void Vox_HasNothingToUnkey()
    {
        var result = SoundModemService.TryForceUnkey(ModemState.Error, () => null, out _);
        Assert.That(result, Is.EqualTo(ForceUnkeyResult.NoPtt));
    }

    [Test]
    public void UnkeyThatThrows_ReportsFailed_AndStillDisposes()
    {
        var ptt = new RecordingPtt(throwOnSet: true);
        var result = SoundModemService.TryForceUnkey(ModemState.Error, () => ptt, out var error);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo(ForceUnkeyResult.Failed));
            Assert.That(error, Is.InstanceOf<IOException>());
            Assert.That(ptt.Disposed, Is.True);
        });
    }
}
