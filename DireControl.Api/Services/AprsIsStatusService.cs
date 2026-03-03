using DireControl.Api.Hubs;
using DireControl.Enums;
using Microsoft.AspNetCore.SignalR;

namespace DireControl.Api.Services;

/// <summary>
/// Tracks the state of the DireControl APRS-IS direct client connection.
/// </summary>
public interface IAprsIsStatusService
{
    AprsIsConnectionState State { get; }
    string? ServerName { get; }
    string ActiveFilter { get; }
    long SessionPacketCount { get; }

    void SetState(AprsIsConnectionState state, string? serverName = null);
    void SetActiveFilter(string filter);
    void IncrementPacketCount();
    void ResetSessionCount();
}

/// <inheritdoc/>
public sealed class AprsIsStatusService(
    IHubContext<PacketHub> hubContext,
    ILogger<AprsIsStatusService> logger) : IAprsIsStatusService
{
    private AprsIsConnectionState _state = AprsIsConnectionState.Disabled;
    private string? _serverName;
    private string _activeFilter = string.Empty;
    private long _sessionPacketCount;

    public AprsIsConnectionState State => _state;
    public string? ServerName => _serverName;
    public string ActiveFilter => _activeFilter;
    public long SessionPacketCount => Interlocked.Read(ref _sessionPacketCount);

    public void SetState(AprsIsConnectionState state, string? serverName = null)
    {
        _state = state;
        if (serverName is not null)
            _serverName = serverName;

        logger.LogInformation("APRS-IS state changed to {State} (server={Server})", state, _serverName ?? "(none)");

        _ = hubContext.Clients.All.SendAsync(
            PacketHub.AprsIsStateChangedMethod,
            BuildDto());
    }

    public void SetActiveFilter(string filter)
    {
        _activeFilter = filter;
    }

    public void IncrementPacketCount()
    {
        Interlocked.Increment(ref _sessionPacketCount);
    }

    public void ResetSessionCount()
    {
        Interlocked.Exchange(ref _sessionPacketCount, 0);
    }

    internal AprsIsStateDto BuildDto() => new()
    {
        State = _state.ToString(),
        ServerName = _serverName,
        ActiveFilter = _activeFilter,
        SessionPacketCount = SessionPacketCount,
    };
}

public sealed class AprsIsStateDto
{
    public required string State { get; init; }
    public string? ServerName { get; init; }
    public string ActiveFilter { get; init; } = string.Empty;
    public long SessionPacketCount { get; init; }
}
