using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent;

/// <summary>
/// Wakes the normal check-in cycle when the server asks the agent to check in sooner.
/// </summary>
public sealed class AgentCheckinSignal
{
    private const int MaximumRecentSignals = 128;

    private readonly SemaphoreSlim signal = new(0, 1);
    private readonly Queue<Guid> recent = new();
    private readonly object gate = new();

    public AgentCheckinReceipt Receive(AgentCheckinCommand request, Guid deviceGuid)
    {
        if (request.RequestId == Guid.Empty || request.DeviceGuid != deviceGuid
            || request.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            return new(request.RequestId, false);

        lock (gate)
        {
            if (!recent.Contains(request.RequestId))
            {
                recent.Enqueue(request.RequestId);

                if (recent.Count > MaximumRecentSignals)
                    recent.Dequeue();

                try
                {
                    signal.Release();
                }
                catch (SemaphoreFullException)
                {
                }
            }
        }

        return new(request.RequestId, true);
    }

    public Task WaitAsync(CancellationToken ct) => signal.WaitAsync(ct);
}