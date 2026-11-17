namespace CloneDeploy_Server.Backend.Services;

public sealed record BootActivityEntry(long Id, DateTimeOffset Time, string Level, string Message);

/// <summary>Bounded diagnostic history independent of persistent logging levels.</summary>
public sealed class BootActivityBuffer
{
    private readonly object gate = new();
    private readonly Queue<BootActivityEntry> entries = new();
    private long sequence;
    public void Add(string level, string message)
    {
        // Keep network-supplied filenames single-line and bounded.
        message = new string(message.Take(2000).Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        lock (gate)
        {
            entries.Enqueue(new(++sequence, DateTimeOffset.UtcNow, level, message));
            while (entries.Count > 500) entries.Dequeue();
        }
    }
    public BootActivityEntry[] Snapshot() { lock (gate) return entries.ToArray(); }
}
