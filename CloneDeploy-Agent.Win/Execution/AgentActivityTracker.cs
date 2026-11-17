using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent;

/// <summary>Tracks overlapping work without allowing a check-in to hide an executing task.</summary>
public sealed class AgentActivityTracker
{
    private readonly object gate = new();
    private readonly Dictionary<long, (AgentActivityKind Kind, Guid? RunId)> active = [];
    private readonly SemaphoreSlim changed = new(0, 1);
    private long nextId;
    private AgentActivity current = new(0, AgentActivityKind.Idle);

    public AgentActivity Current
    {
        get
        {
            lock (gate)
                return current;
        }
    }

    public Task WaitAsync(CancellationToken ct) => changed.WaitAsync(ct);

    public IDisposable Begin(AgentActivityKind kind, Guid? runId = null)
    {
        lock (gate)
        {
            var id = ++nextId;
            active[id] = (kind, runId);
            Update();

            return new Scope(() =>
            {
                lock (gate)
                {
                    active.Remove(id);
                    Update();
                }
            });
        }
    }

    private void Update()
    {
        var selected = active.OrderByDescending(x => Priority(x.Value.Kind)).ThenByDescending(x => x.Key)
            .Select(x => x.Value).FirstOrDefault();

        if (current.Kind == selected.Kind && current.RunId == selected.RunId)
            return;

        current = new(current.Sequence + 1, selected.Kind, selected.RunId);

        try
        {
            changed.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    // Wire values remain stable; display priority is independent of enum numbering.
    private static int Priority(AgentActivityKind kind) => kind switch
    {
        AgentActivityKind.RunningTask or AgentActivityKind.RunningPolicy => 4,
        AgentActivityKind.CollectingInventory => 3,
        AgentActivityKind.EvaluatingPolicies => 2,
        AgentActivityKind.CheckingIn => 1,
        _ => 0
    };

    private sealed class Scope(Action end) : IDisposable
    {
        private Action? complete = end;

        public void Dispose() => Interlocked.Exchange(ref complete, null)?.Invoke();
    }
}

public sealed class AgentActivityWorker(AgentActivityTracker activity, AgentRealtimeClient realtime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await activity.WaitAsync(stoppingToken);
            await realtime.SendActivityAsync(stoppingToken);
        }
    }
}