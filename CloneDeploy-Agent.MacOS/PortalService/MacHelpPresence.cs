using System.Collections.Concurrent;
using CloneDeploy_Agent.Contracts;
namespace CloneDeploy_Agent.MacOS;

// Only live local UI peers renew these leases. Tokens never enter the public status socket.
public sealed class MacHelpPresence(TimeProvider? clock = null)
{
    private const int PresenceTokenCharacters = 43;
    private const int MaximumUsers = 128;

    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<uint, (string Token, DateTimeOffset Seen)> users = new();
    private long revision = DateTime.UtcNow.Ticks;
    public long Watch(uint uid, string token)
    {
        foreach (var entry in users.Where(x => time.GetUtcNow() - x.Value.Seen >= TimeSpan.FromSeconds(60)))
            ((ICollection<KeyValuePair<uint, (string Token, DateTimeOffset Seen)>>)users).Remove(entry);
        if (uid != 0 && token.Length == PresenceTokenCharacters && (users.ContainsKey(uid) || users.Count < MaximumUsers))
            users[uid] = (token, time.GetUtcNow());
        return Interlocked.Read(ref revision);
    }
    public void Remove(uint uid) => users.TryRemove(uid, out _);
    public void Changed() => Interlocked.Increment(ref revision);
    public AgentPortalSessionRequest[] Sessions() => users
        .Where(x => time.GetUtcNow() - x.Value.Seen < TimeSpan.FromSeconds(60))
        .Select(x => new AgentPortalSessionRequest($"macos:{x.Key}", x.Value.Token)).ToArray();
}
