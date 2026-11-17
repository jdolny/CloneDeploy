using System.Net;
using System.Net.Http.Json;
using System.Text;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent.MacOS;

/// <summary>Portable scheduling/history with a deliberately small execution surface.</summary>
public sealed class MacPolicyRunner(MacConfiguration configuration, MacIdentity identity, string directory, MacInventoryHandler inventory)
{
    private const int MaximumResponseBytes = 16 * 1024 * 1024;
    private const int MaximumDetailCharacters = 16000;
    private const int MaximumActionLogCharacters = 14000;

    private readonly PolicyJournal journal = new(configuration.Options(directory));
    public PortalNotificationJournal? Notifications { get; init; }
    public SemaphoreSlim ExecutionGate { get; init; } = new(1, 1);
    public MacActivityTracker? Activity { get; init; }
    public IMacScriptRunner Scripts { get; init; } = new MacScriptRunner(directory);
    public IMacScriptRunner Commands { get; init; } = new MacCommandRunner(directory);
    public MacPackageDeployment Packages { get; init; } = new(configuration.Options(directory));
    public Func<uint, IMacUserActionRunner> UserRunner { get; init; } = uid => new MacUserActionRunner(uid);
    public MacDeviceTools DeviceTools { get; init; } = new();
    public MacLoginTracker? LoginTracker { get; init; }
    private bool recovered;
    private readonly SemaphoreSlim gate = new(1, 1);
    public Task RunAsync(int trigger, CancellationToken ct) => RunCoreAsync(trigger, null, null, ct);
    public Task RunUserAsync(AgentPortalSessionRequest request, AgentPortalUserSession user, CancellationToken ct) =>
        RunCoreAsync((int)PolicyTrigger.Login, request, user, ct);
    public Task RunRequestedAsync(AgentPortalSessionRequest request, AgentPortalUserSession user, int policyId, Guid requestId, CancellationToken ct)
    {
        if (policyId <= 0 || requestId == Guid.Empty) throw new InvalidDataException("Invalid self-service request.");
        return RunCoreAsync(-1, request, user, ct, policyId, requestId);
    }
    internal PolicyRunEntry? RequestedRun(Guid id, string sid) => journal.Read().FirstOrDefault(x => x.RunId == id && x.SelfService && x.UserSid == sid);
    private async Task RunCoreAsync(int trigger, AgentPortalSessionRequest? sessionRequest, AgentPortalUserSession? user, CancellationToken ct, int? requestedPolicy = null, Guid requestId = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            await ExecutionGate.WaitAsync(ct);
            try
            {
                if (DeviceTools.Pending)
                {
                    if (requestedPolicy is not null) throw new IOException("Waiting for the scheduled power action.");
                    return;
                }
                using var evaluation = Activity?.Begin(AgentActivityKind.EvaluatingPolicies);
                var device = identity.State.DeviceGuid ?? throw new InvalidOperationException("Agent is not enrolled.");
                using var certificate = identity.GetClientCertificate() ?? throw new IOException("Missing agent certificate.");
                using var handler = new HttpClientHandler(); var options = configuration.Options(directory);
                AgentServerTrust.Configure(handler, options); handler.ClientCertificates.Add(certificate);
                using var client = new HttpClient(new AgentResponseVerification(handler, options)) { BaseAddress = options.GetServerUri(), Timeout = TimeSpan.FromSeconds(120), MaxResponseContentBufferSize = MaximumResponseBytes };
                if (!recovered)
                {
                    foreach (var run in journal.ReadActive(device))
                        journal.Save(run with { State = ExecutionState.Interrupted, Result = (int)PolicyRunResult.Failed, FinishedAtUtc = DateTime.UtcNow,
                            Detail = "Agent stopped before policy completion. Explicitly rerun this snapshot after review." });
                    recovered = true;
                }
                await ReportAsync(client, device, ct);
                var manual = requestedPolicy is not null;
                if (manual && journal.Contains(requestId)) return;
                List<AgentPolicySnapshot> snapshots;
                if (sessionRequest is null) snapshots = await client.GetFromJsonAsync<List<AgentPolicySnapshot>>("api/agent/v1/policies", ct) ?? [];
                else
                {
                    using var listing = await client.PostAsJsonAsync(manual ? "api/agent/v1/portal-auth/self-service-policies" : "api/agent/v1/portal-auth/policies", sessionRequest, ct);
                    listing.EnsureSuccessStatusCode();
                    snapshots = await listing.Content.ReadFromJsonAsync<List<AgentPolicySnapshot>>(ct) ?? [];
                }
                var candidates = new List<(AgentPolicySnapshot Snapshot, AgentPolicyContent Policy)>();
                foreach (var snapshot in snapshots)
                {
                    try
                    {
                        var policy = PolicySelection.Validate(snapshot);
                        if (manual ? policy.Id == requestedPolicy : PolicySelection.IsDue(snapshot, policy, trigger, (user?.UserSid is { } sid ? journal.ReadLoginHistory(device, policy.Guid, snapshot.Hash, sid) : journal.ReadHistory(device, policy.Guid, snapshot.Hash)), DateTime.UtcNow))
                            candidates.Add((snapshot, policy));
                    }
                    catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
                    { Console.WriteLine($"Ignored unsupported or invalid policy snapshot ({ex.GetType().Name})."); }
                }
                if (manual && candidates.Count != 1) throw new UnauthorizedAccessException("This policy is no longer available to your account.");
                var reboot = false; var rebootIfNoUsers = false;
                foreach (var item in candidates.OrderBy(x => x.Policy.ExecutionPriority).ThenBy(x => x.Policy.Id))
                {
                    ct.ThrowIfCancellationRequested();
                    var policy = item.Policy; var snapshot = item.Snapshot;
                    var run = new PolicyRunEntry(manual ? requestId : Guid.NewGuid(), device, snapshot, ExecutionState.Prepared, DateTime.UtcNow, null, null, null, false, user?.User.Name) { UserSid = user?.UserSid, SelfService = manual };
                    journal.Save(run);
                    string[]? notificationRecipients = null;
                    try
                    {
                        var admission = new AgentPolicyStart(run.RunId, policy.Id, snapshot.Hash) { UserSid = run.UserSid, UserName = run.UserName, SelfService = manual, SupportsNotificationRecipients = true };
                        using var start = sessionRequest is null
                            ? await client.PostAsJsonAsync("api/agent/v1/policies/runs", admission, ct)
                            : await client.PostAsJsonAsync(manual ? "api/agent/v1/portal-auth/self-service-start" : "api/agent/v1/portal-auth/policy-start", new AgentPortalPolicyStart(sessionRequest, admission), ct);
                        start.EnsureSuccessStatusCode();
                        var receipt = await start.Content.ReadFromJsonAsync<AgentPolicyReceipt>(ct) ?? throw new InvalidDataException("Missing policy receipt.");
                        notificationRecipients = receipt.NotificationRecipientSids;
                        if (receipt.RunId != run.RunId || receipt.Completed) throw new InvalidDataException("Unexpected policy admission receipt.");
                    }
                    catch
                    {
                        journal.Save(run with { State = ExecutionState.Completed, Result = (int)PolicyRunResult.Failed, FinishedAtUtc = DateTime.UtcNow, Detail = "Policy admission failed before execution." });
                        throw;
                    }
                    run = run with { State = ExecutionState.Running }; journal.Save(run);
                    using var running = Activity?.Begin(AgentActivityKind.RunningPolicy, run.RunId);
                    var detail = new StringBuilder();
                    int result;
                    try { result = await ExecuteCoreAsync(policy, device, detail, run.RunId, ct, user?.UserSid, client, snapshot.Hash, sessionRequest is null ? null : MacUserActionRunner.ParseUid(sessionRequest.LocalUser), manual, notificationRecipients); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        journal.Save(run with { State = ExecutionState.Interrupted, Result = (int)PolicyRunResult.Failed, FinishedAtUtc = DateTime.UtcNow, Detail = "Agent stopped during policy execution." });
                        throw;
                    }
                    catch (Exception ex) { result = (int)PolicyRunResult.Failed; detail.AppendLine($"Policy failed ({ex.GetType().Name})."); }
                    var text = detail.ToString(); if (text.Length > MaximumDetailCharacters) text = text[..MaximumDetailCharacters];
                    journal.Save(run with { State = ExecutionState.Completed, Result = result, FinishedAtUtc = DateTime.UtcNow, Detail = text });
                    Console.WriteLine($"{DateTimeOffset.UtcNow:O} Policy {policy.Name}: {(PolicyRunResult)result}. {text}");
                    if (result == (int)PolicyRunResult.Success && policy.Settings.Number("ExecutionType") == (int)PolicyExecutionType.Install && policy.Settings.Flag("RemoveInstallCache"))
                    {
                        try { new PolicyCache(options).Remove(snapshot); }
                        catch (Exception ex) { Console.WriteLine($"Could not remove policy cache ({ex.GetType().Name})."); }
                    }
                    await ReportAsync(client, device, ct);
                    if (result == (int)PolicyRunResult.Success && policy.Settings.Number("ExecutionType") == (int)PolicyExecutionType.Install)
                    {
                        reboot |= policy.Settings.Number("CompletedAction") == (int)PolicyCompletedAction.Reboot;
                        rebootIfNoUsers |= policy.Settings.Number("CompletedAction") == (int)PolicyCompletedAction.RebootIfNoLogins;
                    }
                    if (result == (int)PolicyRunResult.Failed && policy.Settings.Number("ErrorAction") == (int)PolicyErrorAction.AbortRemainingPolicies) break;
                }
                try { await DeviceTools.CompletePoliciesAsync(reboot, rebootIfNoUsers, ct); }
                catch (Exception ex) when (!ct.IsCancellationRequested) { Console.Error.WriteLine($"Policy restart could not be scheduled ({ex.GetType().Name})."); }
            }
            finally { ExecutionGate.Release(); }
        }
        finally { gate.Release(); }
    }
    public Task<int> ExecuteAsync(AgentPolicyContent policy, Guid device, StringBuilder detail, CancellationToken ct) => ExecuteCoreAsync(policy, device, detail, Guid.NewGuid(), ct);
    private async Task<int> ExecuteCoreAsync(AgentPolicyContent policy, Guid device, StringBuilder detail, Guid runId, CancellationToken ct, string? targetSid = null, HttpClient? client = null, string? snapshotHash = null, uint? localUid = null, bool manual = false, string[]? notificationRecipients = null)
    {
        var settings = policy.Settings;
        var userContext = !manual && targetSid is not null || PolicyUserContext.UsesUserContext(settings);
        var custom = policy.Actions.Where(x => x.Settings.Flag("AddInventoryCollection")).ToList();
        var conditions = policy.Actions.Where(x => x.Condition is not null).Select(x => x.Condition!).ToList();
        if (policy.Condition is not null) conditions.Add(policy.Condition);
        if (PolicyUserContext.UsesUserContext(settings) && targetSid is null ||
            conditions.Any(x => !MacScriptRunner.Supports(x) || x.Condition is not null || x.Settings.Flag("AddInventoryCollection")) ||
            userContext && localUid is null)
        {
            detail.AppendLine("Ignored: policy requires unsupported conditions or user-context execution.");
            return (int)PolicyRunResult.NotApplicable;
        }
        if (userContext && PolicyUserContext.UsesUserContext(settings)) PolicyUserContext.Validate(policy);
        var userRunner = userContext ? UserRunner(localUid!.Value) : null;
        var collectInventory = settings.Flag("RunInventory") || settings.Number("RunInventory") is >= 1 and <= 3;
        if (custom.Count > InventoryCustomValue.MaximumResults || custom.Any(x => !MacScriptRunner.Supports(x)) || custom.Count > 0 && !collectInventory)
            throw new InvalidDataException("Custom inventory requires supported Mac scripts and Collect inventory enabled.");
        if (policy.Actions.Any(x => x.ConditionNextAction is { } target &&
            (x.Settings.Flag("AddInventoryCollection") || custom.Any(c => c.Guid == target))))
            throw new InvalidDataException("Condition jumps cannot use custom inventory scripts.");
        if (settings.Number("ExecutionType") == (int)PolicyExecutionType.Cache)
        {
            if (client is null || snapshotHash is null) throw new InvalidOperationException("Caching requires an admitted policy run.");
            var snapshot = new AgentPolicySnapshot(1, snapshotHash, false, System.Text.Json.JsonSerializer.SerializeToElement(policy));
            var run = new PolicyRunEntry(runId, device, snapshot, ExecutionState.Running, DateTime.UtcNow, null, null, null, false);
            await new PolicyCache(configuration.Options(directory)).DownloadAsync(client, run, policy, ct);
            detail.AppendLine("Policy files cached; actions were not executed.");
            return (int)PolicyRunResult.Success;
        }
        var unsupported = false;
        var worked = false;
        var failed = false;
        void Log(string text) { if (detail.Length < MaximumActionLogCharacters) detail.AppendLine(text[..Math.Min(text.Length, MaximumActionLogCharacters - detail.Length)]); }
        async Task<MacScriptResult> Script(AgentPolicyAction action, CancellationToken token) =>
            await (userRunner ?? Scripts).RunAsync(action with { Condition = null }, token);
        static int ConditionResult(int behavior) => behavior switch
        {
            (int)PolicyConditionFailedAction.MarkNotApplicable => (int)PolicyRunResult.NotApplicable,
            (int)PolicyConditionFailedAction.MarkSkipped => (int)PolicyRunResult.Skipped,
            (int)PolicyConditionFailedAction.MarkSuccess => (int)PolicyRunResult.Success,
            _ => (int)PolicyRunResult.Failed
        };
        async Task<bool> Condition(AgentPolicyAction action)
        {
            var result = await Script(action, ct); Log(result.Detail); return result.Success;
        }
        if (policy.Condition is { } condition && !await Condition(condition)) return ConditionResult(settings.Number("ConditionFailedAction"));
        async Task Collect()
        {
            var receipt = await inventory.CollectAsync(new(Guid.NewGuid(), device, DateTimeOffset.UtcNow.AddMinutes(5)), device,
                (collected, token) => CollectCustomAsync(policy, runId, collected, Script, token), ct);
            Log(receipt.Detail);
            if (!receipt.Saved) throw new IOException("Inventory failed.");
        }
        if (settings.Number("RunInventory") is (int)PolicyInventoryAction.Before or (int)PolicyInventoryAction.Both) await Collect();
        var ordered = policy.Actions.Where(x => !x.Settings.Flag("AddInventoryCollection")).OrderBy(x => x.Order).ThenBy(x => x.Guid).ToList();
        var steps = 0;
        for (var index = 0; index < ordered.Count; index++)
        {
            if (++steps > Math.Max(100, ordered.Count * 4)) throw new InvalidDataException("Policy condition jump loop detected.");
            ct.ThrowIfCancellationRequested();
            var action = ordered[index];
            if (action.Condition is { } guard && !await Condition(guard))
            {
                if (action.ConditionFailedAction != (int)PolicyConditionFailedAction.GotoModule) return ConditionResult(action.ConditionFailedAction);
                var target = ordered.FindIndex(x => x.Guid == action.ConditionNextAction);
                if (target < 0) throw new InvalidDataException("Policy jump target was not found.");
                index = target - 1; continue;
            }
            action = action with { Condition = null };
            if (MacScriptRunner.Supports(action) || MacPackageDeployment.Supports(action) || action.Type is (int)ActionType.Command or (int)ActionType.File)
            {
                worked = true;
                MacScriptResult result;
                try
                {
                    result = userRunner is not null
                        ? action.Type == (int)ActionType.File || MacPackageDeployment.Supports(action)
                            ? await userRunner.RunFilesAsync(action, configuration.Options(directory), client ?? throw new InvalidOperationException("Files require an admitted run."), runId, device, snapshotHash!, ct)
                            : await userRunner.RunAsync(action, ct)
                        : action.Type == (int)ActionType.File
                        ? await new MacFileDeployment(configuration.Options(directory)).RunAsync(action,
                            client ?? throw new InvalidOperationException("File deployment requires an admitted policy run."), runId, device,
                            snapshotHash!, $"api/agent/v1/policies/runs/{runId}", ct)
                        : MacPackageDeployment.Supports(action)
                            ? await Packages.RunAsync(action, client ?? throw new InvalidOperationException("Package deployment requires an admitted policy run."),
                                runId, device, snapshotHash!, $"api/agent/v1/policies/runs/{runId}", ct)
                            : await (action.Type == (int)ActionType.Command ? Commands : Scripts).RunAsync(action, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { result = new(false, $"{action.Name}: {ex.Message}"); }
                Log(result.Detail);
                if (!result.Success)
                {
                    failed = true;
                    if (settings.Number("ErrorAction") != (int)PolicyErrorAction.Continue) break;
                }
                continue;
            }
            if (action.Type == (int)ActionType.Notification && Notifications is not null)
            {
                Notifications.StoreForRecipients(PortalNotice.Create(action.Settings, runId, action.Guid), notificationRecipients, targetSid);
                Log($"Notification queued: {action.Name}. Delivery does not confirm that a user has read it.");
                worked = true; continue;
            }
            Log($"Ignored unsupported action: {action.Name} (type {action.Type})."); unsupported = true;
        }
        if (settings.Flag("RunInventory") || settings.Number("RunInventory") is (int)PolicyInventoryAction.After or (int)PolicyInventoryAction.Both) await Collect();
        if (settings.Flag("RunLoginTracker"))
        {
            worked = true;
            if (LoginTracker is null || !await LoginTracker.SampleAsync(ct)) { failed = true; Log("Login history could not be sampled."); }
            else Log("Login history sampled; upload is included with check-in.");
        }
        if (settings.Number("CompletedAction") is (int)PolicyCompletedAction.Reboot or (int)PolicyCompletedAction.RebootIfNoLogins) worked = true;
        if (settings.Number("RemoteAccess") != 0 || settings.Number("CompletedAction") is < 0 or > 2)
        { Log("Ignored unsupported remote access or completion behavior."); unsupported = true; }
        if (!collectInventory && !unsupported && !worked) { Log("No supported work in this policy."); unsupported = true; }
        return failed ? (int)PolicyRunResult.Failed : unsupported ? (int)PolicyRunResult.NotApplicable : (int)PolicyRunResult.Success;
    }
    internal static async Task CollectCustomAsync(AgentPolicyContent policy, Guid runId, AgentInventory inventory,
        Func<AgentPolicyAction, CancellationToken, Task<MacScriptResult>> execute, CancellationToken ct)
    {
        var scripts = policy.Actions.Where(x => x.Settings.Flag("AddInventoryCollection")).OrderBy(x => x.Order).ThenBy(x => x.Guid).ToList();
        if (scripts.Count == 0) return;
        var results = new List<InventoryCustomValue>(); var unavailable = 0;
        foreach (var script in scripts)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (script.Condition is { } condition && !(await execute(condition, ct)).Success) { unavailable++; continue; }
                var result = await execute(script, ct);
                var value = result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim();
                if (!result.Success || result.Output.Length >= InventoryCustomValue.MaximumLength || string.IsNullOrEmpty(value)) unavailable++;
                else results.Add(new(script.Guid, value));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { unavailable++; }
        }
        inventory.CustomInventory = new(runId, results);
        if (unavailable > 0) inventory.Warnings.Add($"Custom inventory: {unavailable} script(s) unavailable; previous values retained");
    }
    private async Task ReportAsync(HttpClient client, Guid device, CancellationToken ct)
    {
        foreach (var (start, result) in journal.ReadReportPayloads(device))
        {
            try
            {
                using var admission = await client.PostAsJsonAsync("api/agent/v1/policies/runs", start, ct);
                if (admission.StatusCode == HttpStatusCode.NotFound) { journal.MarkReported(start.RunId); continue; }
                admission.EnsureSuccessStatusCode();
                using var response = await client.PostAsJsonAsync("api/agent/v1/policies/results", result, ct);
                response.EnsureSuccessStatusCode(); journal.MarkReported(start.RunId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { Console.Error.WriteLine($"Policy result {start.RunId} remains queued ({ex.GetType().Name})."); }
        }
    }
}
