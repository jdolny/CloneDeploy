using System.Collections.Concurrent;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>Ephemeral Help subscriptions, scoped to a live device connection.</summary>
public sealed class HelpPresenceService(IServiceScopeFactory scopes, IHubContext<AgentHub> hub,
    TimeProvider clock, ILogger<HelpPresenceService> logger)
{
    private sealed record User(string Sid, string[] Keys);
    private sealed record Presence(int DeviceId, User[] Users, DateTimeOffset Seen, DateTimeOffset Resolved);
    private readonly ConcurrentDictionary<string, Presence> connections = new();

    public void Remove(string connection) => connections.TryRemove(connection, out _);
    public async Task RegisterAsync(int deviceId, string connection, string[] sids, CancellationToken ct)
    {
        Prune();
        if (sids is null || sids.Length > 128 || sids.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 184))
            return;
        if (sids.Length == 0) { Remove(connection); return; }
        using var scope = scopes.CreateScope();
        // AgentHub has already revalidated certificate and connection ownership in Heartbeat.
        foreach (var old in connections.Where(x => x.Value.DeviceId == deviceId && x.Key != connection).Select(x => x.Key)) Remove(old);
        var now = clock.GetUtcNow();
        if (connections.TryGetValue(connection, out var previous) && now - previous.Resolved < TimeSpan.FromMinutes(previous.Users.Any(x => x.Keys.Length == 0) ? 1 : 5)
            && previous.Users.Select(x => x.Sid).Order().SequenceEqual(sids.Distinct().Order()))
        {
            connections[connection] = previous with { Seen = now };
            return;
        }
        await using var db = await scope.ServiceProvider.GetRequiredService<ICloneDeployDbFactory>().CreateContextAsync();
        var users = new List<User>();
        foreach (var sid in sids.Distinct())
        {
            try { var identity = await HelpDeskService.Identity(db, sid, ct); users.Add(new(sid, identity.Keys)); }
            catch (InvalidOperationException) { users.Add(new(sid, [])); }
        }
        connections[connection] = new(deviceId, users.ToArray(), now, now);
        // First registration / directory identity change may follow an offline update.
        if (previous is null || !previous.Users.SelectMany(x => x.Keys).Order().SequenceEqual(users.SelectMany(x => x.Keys).Order()))
            await hub.Clients.Client(connection).SendAsync(AgentHubProtocol.HelpChanged, users.Where(x => x.Keys.Length > 0).Select(x => x.Sid).ToArray(), ct);
    }

    private void Prune()
    {
        var cutoff = clock.GetUtcNow() - AgentConnectionService.PresenceLifetime;
        foreach (var entry in connections.Where(x => x.Value.Seen <= cutoff))
            ((ICollection<KeyValuePair<string, Presence>>)connections).Remove(entry);
    }

    public async Task RefreshAsync(int[] ticketIds, CancellationToken ct)
    {
        Prune();
        if (connections.IsEmpty) return;
        using var scope = scopes.CreateScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<ICloneDeployDbFactory>().CreateContextAsync();
        if (!(await AgentFeatureSettings.FeaturesAsync(db, ct)).HelpDeskEnabled) return;
        var all = ticketIds.Contains(0);
        var keys = all ? [] : (await db.HelpTickets.Where(x => ticketIds.Contains(x.Id)).Select(x => x.RequesterKeys).ToArrayAsync(ct)).SelectMany(x => x).ToHashSet();
        foreach (var entry in connections)
        {
            var sids = entry.Value.Users.Where(x => x.Keys.Length > 0 && (all || x.Keys.Any(keys.Contains))).Select(x => x.Sid).ToArray();
            if (sids.Length == 0) continue;
            try
            {
                var active = await scope.ServiceProvider.GetRequiredService<AgentConnectionService>().FindActiveAsync(entry.Value.DeviceId, ct);
                if (active?.ConnectionId != entry.Key) { Remove(entry.Key); continue; }
                await hub.Clients.Client(entry.Key).SendAsync(AgentHubProtocol.HelpChanged, sids, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested) { logger.LogDebug(ex, "Help update signal could not be delivered."); }
        }
    }
}
