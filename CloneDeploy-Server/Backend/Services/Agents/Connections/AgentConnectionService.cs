using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Tracks which device agents are connected and removes outdated connection records.
/// </summary>
public sealed class AgentConnectionService(ICloneDeployDbFactory factory, AgentServerInstance server, TimeProvider clock)
{
    public static readonly TimeSpan PresenceLifetime = TimeSpan.FromSeconds(90);

    public async Task ConnectAsync(int deviceId, Guid certificateId, string connectionId, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        if (!await ActiveCertificates(db).AnyAsync(x => x.Id == certificateId && x.DeviceId == deviceId, ct))
            throw new UnauthorizedAccessException("The agent certificate is no longer active.");

        var now = clock.GetUtcNow().UtcDateTime;
        // DeviceId already has a unique index. Reconnection atomically replaces the old connection.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "SocketConnections" ("DeviceId", "ConnectionId", "ServerInstanceId", "AgentCertificateId", "ConnectedAtUtc", "LastSeenAtUtc")
            VALUES ({deviceId}, {connectionId}, {server.Id}, {certificateId}, {now}, {now})
            ON CONFLICT ("DeviceId") DO UPDATE SET
                "ConnectionId" = EXCLUDED."ConnectionId", "ServerInstanceId" = EXCLUDED."ServerInstanceId",
                "AgentCertificateId" = EXCLUDED."AgentCertificateId", "ConnectedAtUtc" = EXCLUDED."ConnectedAtUtc",
                "LastSeenAtUtc" = EXCLUDED."LastSeenAtUtc"
            """, ct);
    }

    public async Task<bool> HeartbeatAsync(int deviceId, Guid certificateId, string connectionId, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        var now = clock.GetUtcNow().UtcDateTime;
        // Recheck database authorization on long-lived connections, not only during the TLS handshake.
        var certificates = ActiveCertificates(db);
        return await db.SocketConnections.Where(x => x.DeviceId == deviceId && x.ConnectionId == connectionId
                && x.ServerInstanceId == server.Id && x.AgentCertificateId == certificateId
                && certificates.Any(c => c.Id == certificateId && c.DeviceId == deviceId))
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.LastSeenAtUtc, now), ct) == 1;
    }

    public async Task DisconnectAsync(string connectionId)
    {
        await using var db = await factory.CreateContextAsync();
        // An old socket disconnecting must not delete a newer socket for the same device.
        await db.SocketConnections.Where(x => x.ConnectionId == connectionId && x.ServerInstanceId == server.Id).ExecuteDeleteAsync();
    }

    public async Task<SocketConnection?> FindActiveAsync(int deviceId, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        var cutoff = clock.GetUtcNow().UtcDateTime - PresenceLifetime;
        var certificates = ActiveCertificates(db);
        return await db.SocketConnections.AsNoTracking().SingleOrDefaultAsync(x => x.DeviceId == deviceId
            && x.LastSeenAtUtc > cutoff && certificates.Any(c => c.Id == x.AgentCertificateId && c.DeviceId == deviceId), ct);
    }

    public async Task RemoveStaleAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        var cutoff = clock.GetUtcNow().UtcDateTime - PresenceLifetime;
        await db.SocketConnections.Where(x => x.LastSeenAtUtc <= cutoff).ExecuteDeleteAsync(ct);
    }

    public async Task RemoveOwnedAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        await db.SocketConnections.Where(x => x.ServerInstanceId == server.Id).ExecuteDeleteAsync(ct);
    }

    private IQueryable<AgentCertificate> ActiveCertificates(CloneDeployDbContext db)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return db.AgentCertificates.Where(x => x.RevokedAtUtc == null && (x.RetireAfterUtc == null || x.RetireAfterUtc > now) && x.NotBeforeUtc <= now && x.NotAfterUtc > now
            && !x.Issuer.IsRevoked && x.Issuer.Type == CertificateType.Authority
            && x.Issuer.NotBeforeUtc <= now && x.Issuer.NotAfterUtc > now
            && x.Device.ArchivedAtUtc == null && x.Device.ProvisionStatus == DeviceProvisionStatus.Provisioned);
    }
}
