using System.Diagnostics;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent;

/// <summary>Short local leases from verified portal callers; no ticket data is retained.</summary>
public sealed class AgentHelpPresence(TimeProvider clock)
{
    private readonly object gate = new();
    private sealed record Portal(DateTimeOffset Seen, Process? Process);
    private readonly Dictionary<(string Sid, int Session), Portal> portals = [];
    private readonly Dictionary<string, long> revisions = [];
    private long revision;

    public long Touch(string sid, int session, int processId = 0)
    {
        lock (gate)
        {
            Prune();
            portals.TryGetValue((sid, session), out var previous);
            var process = previous?.Process;
            if (processId != 0 && process?.Id != processId)
            {
                process?.Dispose();
                process = Process.GetProcessById(processId);
                // Retain the native handle so PID reuse cannot revive a closed portal.
                _ = process.Handle;
            }
            portals[(sid, session)] = new(clock.GetUtcNow(), process);
            revisions.TryAdd(sid, revision);
            return revisions[sid];
        }
    }
    public string[] Current()
    {
        lock (gate) { Prune(); return portals.Keys.Select(x => x.Sid).Distinct().ToArray(); }
    }
    public void Changed(string[] sids)
    {
        lock (gate)
        {
            Prune();
            var next = ++revision;
            foreach (var sid in portals.Keys.Select(x => x.Sid).Distinct())
                if (sids.Length == 0 || sids.Contains(sid)) revisions[sid] = next;
        }
    }
    private void Prune()
    {
        var cutoff = clock.GetUtcNow().AddSeconds(-45);
        foreach (var key in portals.Where(x => x.Value.Seen <= cutoff || x.Value.Process?.HasExited == true).Select(x => x.Key).ToArray())
        {
            portals[key].Process?.Dispose();
            portals.Remove(key);
        }
        foreach (var sid in revisions.Keys.Where(sid => !portals.Keys.Any(x => x.Sid == sid)).ToArray()) revisions.Remove(sid);
    }
}
