using DireControl.Api.Services;
using DireControl.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace DireControl.Tests;

[TestFixture]
public class AprsIsStatusServiceTests
{
    private static AprsIsStatusService CreateService() =>
        new(NullLogger<AprsIsStatusService>.Instance);

    [Test]
    public void RecordFailure_StampsFirstDisconnectedOnce_AndCountsAttempts()
    {
        var svc = CreateService();

        svc.RecordFailure("connection refused");
        var firstStamp = svc.FirstDisconnectedAt;

        svc.RecordFailure("timeout");

        Assert.Multiple(() =>
        {
            Assert.That(svc.FailedAttempts, Is.EqualTo(2));
            Assert.That(svc.LastError, Is.EqualTo("timeout"));
            // The first-disconnected timestamp marks the start of the outage and
            // must not move on subsequent failures within the same outage.
            Assert.That(svc.FirstDisconnectedAt, Is.EqualTo(firstStamp));
        });
    }

    [Test]
    public void RecordConnectAttempt_StampsLastAttempt()
    {
        var svc = CreateService();
        Assert.That(svc.LastConnectAttemptAt, Is.Null);

        svc.RecordConnectAttempt();

        Assert.That(svc.LastConnectAttemptAt, Is.Not.Null);
    }

    [Test]
    public void SetState_Connected_ClearsOutageDiagnostics()
    {
        var svc = CreateService();
        svc.RecordFailure("boom");
        svc.RecordFailure("boom again");

        svc.SetState(AprsIsConnectionState.Connected, "T2TEST");

        Assert.Multiple(() =>
        {
            Assert.That(svc.FailedAttempts, Is.Zero);
            Assert.That(svc.LastError, Is.Null);
            Assert.That(svc.FirstDisconnectedAt, Is.Null);
            Assert.That(svc.ServerName, Is.EqualTo("T2TEST"));
        });
    }

    [Test]
    public void SetState_NonConnected_PreservesOutageDiagnostics()
    {
        var svc = CreateService();
        svc.RecordFailure("dropped");

        svc.SetState(AprsIsConnectionState.Disconnected);

        Assert.Multiple(() =>
        {
            Assert.That(svc.FailedAttempts, Is.EqualTo(1));
            Assert.That(svc.LastError, Is.EqualTo("dropped"));
            Assert.That(svc.FirstDisconnectedAt, Is.Not.Null);
        });
    }
}
