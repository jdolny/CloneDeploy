using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>Reads activity in batches and delivers only changed snapshots to interested pages.</summary>
public sealed class DeviceActivityFeed(IServiceScopeFactory scopes, TimeProvider clock, ILogger<DeviceActivityFeed> logger)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Subscription> subscriptions = new();
    private readonly Dictionary<int, DeviceActivitySnapshot> last = [];

    public async Task<IDisposable> SubscribeAsync(int[] deviceIds, Func<IReadOnlyList<DeviceActivitySnapshot>, Task> receive)
    {
        var subscription = new Subscription(deviceIds.ToHashSet(), receive, subscriptions);
        await gate.WaitAsync();
        try
        {
            // Register before reading so changes during the initial query are processed afterwards.
            subscriptions[subscription.Id] = subscription;
            var snapshots = await ReadAsync(subscription.Devices.ToArray(), CancellationToken.None);
            // A new viewer must not consume a pending change on behalf of existing viewers.
            foreach (var snapshot in snapshots) last.TryAdd(snapshot.DeviceId, snapshot);
            await receive(snapshots);
            return subscription;
        }
        catch { subscription.Dispose(); throw; }
        finally { gate.Release(); }
    }

    public async Task RefreshAsync(int[] changedIds, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var watched = subscriptions.Values.SelectMany(x => x.Devices).ToHashSet();
            foreach (var id in last.Keys.Where(id => !watched.Contains(id)).ToArray()) last.Remove(id);
            var ids = changedIds.Contains(0) ? watched.ToArray() : changedIds.Where(watched.Contains).Distinct().ToArray();
            if (ids.Length == 0) return;
            var snapshots = await ReadAsync(ids, ct);
            var changed = snapshots.Where(x => !last.TryGetValue(x.DeviceId, out var old) || old != x).ToArray();
            foreach (var snapshot in changed) last[snapshot.DeviceId] = snapshot;
            foreach (var subscription in subscriptions.Values)
            {
                var relevant = changed.Where(x => subscription.Devices.Contains(x.DeviceId)).ToArray();
                if (relevant.Length == 0) continue;
                try { await subscription.Receive(relevant); }
                catch (Exception ex) { logger.LogDebug(ex, "Device activity subscriber closed."); subscription.Dispose(); }
            }
        }
        finally { gate.Release(); }
    }

    private async Task<List<DeviceActivitySnapshot>> ReadAsync(int[] ids, CancellationToken ct)
    {
        if (ids.Length == 0) return [];
        using var scope = scopes.CreateScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<ICloneDeployDbFactory>().CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var cutoff = now - AgentConnectionService.PresenceLifetime;
        var devices = await db.Devices.Where(x => ids.Contains(x.Id))
            .Select(x => new { x.Id, x.Name, x.ArchivedAtUtc, x.ClientVersion, x.LastCheckinAtUtc, x.LastKnownIp }).ToListAsync(ct);
        var connections = await db.SocketConnections.AsNoTracking().Where(x => ids.Contains(x.DeviceId)
            && x.LastSeenAtUtc > cutoff && x.AgentCertificate != null && x.AgentCertificate.RevokedAtUtc == null
            && (x.AgentCertificate.RetireAfterUtc == null || x.AgentCertificate.RetireAfterUtc > now)
            && x.AgentCertificate.NotBeforeUtc <= now && x.AgentCertificate.NotAfterUtc > now
            && !x.AgentCertificate.Issuer.IsRevoked && x.AgentCertificate.Issuer.NotAfterUtc > now).ToDictionaryAsync(x => x.DeviceId, ct);
        var reports = await db.Set<DeviceActivityReport>().AsNoTracking().Where(x => ids.Contains(x.DeviceId)).ToDictionaryAsync(x => x.DeviceId, ct);
        var imaging = await db.ActiveImagingTasks.AsNoTracking().Where(x => ids.Contains(x.DeviceId)).ToDictionaryAsync(x => x.DeviceId, ct);
        var queued = await db.DeviceTasks.Where(x => ids.Contains(x.DeviceId) &&
                (x.Status == DeviceTaskStatus.Queued || x.Status == DeviceTaskStatus.Offered) && x.Deployment.ExpiresAtUtc > now)
            .OrderBy(x => x.Deployment.CreatedAtUtc).Select(x => new { x.DeviceId, x.Deployment.ActionName, x.Deployment.Timing }).ToListAsync(ct);
        return devices.Select(device =>
        {
            var socket = connections.GetValueOrDefault(device.Id);
            var queue = queued.FirstOrDefault(x => x.DeviceId == device.Id);
            var snapshot = device.ArchivedAtUtc is not null ? new DeviceActivitySnapshot(device.Id, false, "Archived", null)
                : DeviceActivitySnapshot.Create(device.Id, socket is not null, socket?.ConnectionId,
                    reports.GetValueOrDefault(device.Id), imaging.GetValueOrDefault(device.Id), queue?.ActionName, queue?.Timing);
            return snapshot with { DeviceName = device.Name, ClientVersion = device.ClientVersion, LastCheckinAtUtc = device.LastCheckinAtUtc, LastKnownIp = device.LastKnownIp };
        }).Concat(ids.Except(devices.Select(x => x.Id)).Select(id => new DeviceActivitySnapshot(id, false, "Device unavailable", null))).ToList();
    }

    private sealed class Subscription(HashSet<int> devices, Func<IReadOnlyList<DeviceActivitySnapshot>, Task> receive,
        System.Collections.Concurrent.ConcurrentDictionary<Guid, Subscription> owners) : IDisposable
    {
        public Guid Id { get; } = Guid.NewGuid();
        public HashSet<int> Devices { get; } = devices;
        public Func<IReadOnlyList<DeviceActivitySnapshot>, Task> Receive { get; } = receive;
        public void Dispose() => owners.TryRemove(Id, out _);
    }
}

public sealed class DeviceActivityService(DeviceActivityFeed feed, AuthenticationStateProvider authentication, IAuthorizationService authorization)
{
    public async Task<IDisposable> SubscribeAsync(int[] ids, Func<IReadOnlyList<DeviceActivitySnapshot>, Task> receive)
    {
        async Task<bool> Allowed() => (await authorization.AuthorizeAsync(
            (await authentication.GetAuthenticationStateAsync()).User, Constants.Permissions.DeviceView)).Succeeded;
        if (!await Allowed()) throw new UnauthorizedAccessException();
        return await feed.SubscribeAsync(ids, async snapshots =>
        {
            if (!await Allowed()) throw new UnauthorizedAccessException();
            await receive(snapshots);
        });
    }
}
