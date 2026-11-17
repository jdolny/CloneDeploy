namespace CloneDeploy_Server.Backend.Services;

/// <summary>Coalesced post-commit notifications; reconciliation also recovers missed notifications.</summary>
public sealed class NativePxeChanges
{
    private readonly object _gate = new();
    private readonly HashSet<int> _devices = [];
    private bool _rules = true;
    private bool _reconcile;
    public void RulesChanged() { lock (_gate) _rules = true; }
    public void DeviceChanged(int id) { lock (_gate) { if (_devices.Count < 10000) _devices.Add(id); else _reconcile = true; } }
    public bool TakeReconciliation() { lock (_gate) { var value = _reconcile; _reconcile = false; return value; } }
    public (bool Rules, int[] Devices) Drain()
    {
        lock (_gate) { var result = (_rules, _devices.ToArray()); _rules = false; _devices.Clear(); return result; }
    }
}
