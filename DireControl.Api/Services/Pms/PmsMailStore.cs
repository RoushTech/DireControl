using DireControl.Data;
using DireControl.Data.Models;
using DireControl.Enums;
using Microsoft.EntityFrameworkCore;

namespace DireControl.Api.Services.Pms;

/// <summary>EF-backed <see cref="IPmsMailStore"/> (singleton; scopes per call).</summary>
public sealed class PmsMailStore(IServiceScopeFactory scopeFactory) : IPmsMailStore
{
    public async Task<List<PmsMessage>> ListVisibleAsync(string callerBase, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
        return await db.PmsMessages
            .AsNoTracking()
            .Where(m => !m.IsKilled && (
                m.ToCallsign == callerBase ||
                m.FromCallsign == callerBase ||
                m.Type == PmsMessageType.Bulletin))
            .OrderByDescending(m => m.Id)
            .ToListAsync(ct);
    }

    public async Task<PmsMessage?> GetAsync(int id, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
        return await db.PmsMessages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);
    }

    public async Task<PmsMessage> SaveAsync(PmsMessage message, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
        db.PmsMessages.Add(message);
        await db.SaveChangesAsync(ct);
        return message;
    }

    public async Task MarkReadAsync(int id, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
        var message = await db.PmsMessages.FindAsync([id], ct);
        if (message is not null && message.ReadAt is null)
        {
            message.ReadAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<bool> KillAsync(int id, string callerBase, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
        var message = await db.PmsMessages.FindAsync([id], ct);
        if (message is null || message.IsKilled)
            return false;
        if (message.FromCallsign != callerBase && message.ToCallsign != callerBase)
            return false;

        message.IsKilled = true;
        message.KilledAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> KillReadMailAsync(string callerBase, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
        var read = await db.PmsMessages
            .Where(m => !m.IsKilled
                && m.Type == PmsMessageType.Private
                && m.ToCallsign == callerBase
                && m.ReadAt != null)
            .ToListAsync(ct);
        foreach (var message in read)
        {
            message.IsKilled = true;
            message.KilledAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        return read.Count;
    }

    public async Task<int> CountUnreadForAsync(string callerBase, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
        return await db.PmsMessages.CountAsync(
            m => !m.IsKilled
                && m.Type == PmsMessageType.Private
                && m.ToCallsign == callerBase
                && m.ReadAt == null,
            ct);
    }
}
