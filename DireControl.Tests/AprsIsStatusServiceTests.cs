using DireControl.Api.Hubs;
using DireControl.Api.Services;
using DireControl.Enums;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace DireControl.Tests;

[TestFixture]
public class AprsIsStatusServiceTests
{
    private static AprsIsStatusService CreateService() =>
        new(new FakeHubContext(), NullLogger<AprsIsStatusService>.Instance);

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

    // ─── Minimal SignalR fakes (no mocking library is referenced) ─────────────

    private sealed class FakeHubContext : IHubContext<PacketHub>
    {
        public IHubClients Clients { get; } = new FakeHubClients();
        public IGroupManager Groups { get; } = new FakeGroupManager();
    }

    private sealed class FakeHubClients : IHubClients
    {
        public IClientProxy All { get; } = new FakeClientProxy();
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => All;
        public IClientProxy Client(string connectionId) => All;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => All;
        public IClientProxy Group(string groupName) => All;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => All;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => All;
        public IClientProxy User(string userId) => All;
        public IClientProxy Users(IReadOnlyList<string> userIds) => All;
    }

    private sealed class FakeClientProxy : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
