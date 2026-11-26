using DireControl.Data.Models;

namespace DireControl.Api.Services.Pms;

/// <summary>Summary of a recently RF-heard station for the PMS J command.</summary>
public sealed record HeardStation(string Callsign, DateTime LastHeardUtc);

/// <summary>
/// Mail storage behind the PMS command processor — kept as an interface so
/// the line-protocol logic tests can run against the real EF-backed store or
/// nothing at all.
/// </summary>
public interface IPmsMailStore
{
    /// <summary>
    /// Mail visible to <paramref name="callerBase"/>: addressed to them, sent
    /// by them, or bulletins — killed mail excluded — newest first.
    /// </summary>
    Task<List<PmsMessage>> ListVisibleAsync(string callerBase, CancellationToken ct = default);

    Task<PmsMessage?> GetAsync(int id, CancellationToken ct = default);

    Task<PmsMessage> SaveAsync(PmsMessage message, CancellationToken ct = default);

    /// <summary>Marks the message read by its addressee.</summary>
    Task MarkReadAsync(int id, CancellationToken ct = default);

    /// <summary>Kills a message when the caller is its sender or addressee.</summary>
    Task<bool> KillAsync(int id, string callerBase, CancellationToken ct = default);

    /// <summary>Kills every read private message addressed to the caller; returns the count.</summary>
    Task<int> KillReadMailAsync(string callerBase, CancellationToken ct = default);

    /// <summary>Unread private mail count for the connect banner.</summary>
    Task<int> CountUnreadForAsync(string callerBase, CancellationToken ct = default);
}
