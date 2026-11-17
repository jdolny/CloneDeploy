using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent;

internal static class CustomInventoryCollector
{
    public static async Task CollectAsync(
        AgentPolicyContent policy,
        Guid runId,
        AgentInventory inventory,
        Func<AgentPolicyAction, CancellationToken, Task<PolicyProcessResult>> execute,
        CancellationToken ct)
    {
        if (!(policy.Settings.Flag("RunInventory") || policy.Settings.Number("RunInventory") is >= 1 and <= 3))
            return;

        var scripts = policy.Actions.Where(x => x.Type == (int)ActionType.Script && x.Settings.Flag("AddInventoryCollection"))
            .OrderBy(x => x.Order).ThenBy(x => x.Guid).ToList();
        if (scripts.Count == 0)
            return;

        var results = new List<InventoryCustomValue>();
        var unavailable = 0;

        foreach (var script in scripts)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (script.Condition is { } condition && !PolicyActionRunner.Succeeded(condition, await execute(condition, ct)))
                {
                    unavailable++;
                    continue;
                }

                var result = await execute(script, ct);
                // Process output is bounded. Never publish a value from an output prefix that may be truncated.
                if (!PolicyActionRunner.Succeeded(script, result) || result.Output.Length >= InventoryCustomValue.MaximumLength)
                {
                    unavailable++;
                    continue;
                }

                var value = result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .LastOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim();
                if (string.IsNullOrEmpty(value))
                    unavailable++;
                else
                    results.Add(new(script.Guid, value));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                unavailable++;
            }
        }

        inventory.CustomInventory = new(runId, results);
        if (unavailable > 0)
            inventory.Warnings.Add($"Custom inventory: {unavailable} script(s) unavailable; previous values retained");
    }
}
