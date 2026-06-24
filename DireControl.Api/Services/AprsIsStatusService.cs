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

    /// <summary>UTC time the connection first dropped in the current outage; null while connected.</summary>
    DateTime? FirstDisconnectedAt { get; }

    /// <summary>UTC time of the most recent connection attempt.</summary>
    DateTime? LastConnectAttemptAt { get; }

    /// <summary>Number of consecutive failed connection attempts since the last successful connect.</summary>
    int FailedAttempts { get; }

    /// <summary>Message from the most recent connection failure; null while connected.</summary>
    string? LastError { get; }

    void SetState(AprsIsConnectionState state, string? serverName = null);
    void SetActiveFilter(string filter);
    void IncrementPacketCount();
    void ResetSessionCount();

    /// <summary>Records that a connection attempt is starting (stamps <see cref="LastConnectAttemptAt"/>).</summary>
    void RecordConnectAttempt();

    /// <summary>
    /// Records a failed connection attempt: increments <see cref="FailedAttempts"/>, captures the
    /// error, and stamps <see cref="FirstDisconnectedAt"/> if this is the first failure of an outage.
    /// </summary>
    void RecordFailure(string error);
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
    private DateTime? _firstDisconnectedAt;
    private DateTime? _lastConnectAttemptAt;
    private int _failedAttempts;
    private string? _lastError;

    public AprsIsConnectionState State => _state;
    public string? ServerName => _serverName;
    public string ActiveFilter => _activeFilter;
    public long SessionPacketCount => Interlocked.Read(ref _sessionPacketCount);
    public DateTime? FirstDisconnectedAt => _firstDisconnectedAt;
    public DateTime? LastConnectAttemptAt => _lastConnectAttemptAt;
    public int FailedAttempts => _failedAttempts;
    public string? LastError => _lastError;

    public void SetState(AprsIsConnectionState state, string? serverName = null)
    {
        _state = state;
        if (serverName is not null)
            _serverName = serverName;

        // A successful connection clears the outage diagnostics.
        if (state == AprsIsConnectionState.Connected)
        {
            _firstDisconnectedAt = null;
            _failedAttempts = 0;
            _lastError = null;
        }

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

    public void RecordConnectAttempt()
    {
        _lastConnectAttemptAt = DateTime.UtcNow;
    }

    public void RecordFailure(string error)
    {
        _failedAttempts++;
        _lastError = error;
        _firstDisconnectedAt ??= DateTime.UtcNow;
    }

    internal AprsIsStateDto BuildDto() => new()
    {
        State = _state.ToString(),
        ServerName = _serverName,
        ActiveFilter = _activeFilter,
        SessionPacketCount = SessionPacketCount,
        FirstDisconnectedAt = _firstDisconnectedAt,
        LastConnectAttemptAt = _lastConnectAttemptAt,
        FailedAttempts = _failedAttempts,
        LastError = _lastError,
    };
}

public sealed class AprsIsStateDto
{
    public required string State { get; init; }
    public string? ServerName { get; init; }
    public string ActiveFilter { get; init; } = string.Empty;
    public long SessionPacketCount { get; init; }
    public DateTime? FirstDisconnectedAt { get; init; }
    public DateTime? LastConnectAttemptAt { get; init; }
    public int FailedAttempts { get; init; }
    public string? LastError { get; init; }
}
