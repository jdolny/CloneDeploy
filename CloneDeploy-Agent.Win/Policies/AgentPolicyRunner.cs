using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent;

/// <summary>
/// Coordinates assigned policies, runs the ones that are due, and reports their results.
/// </summary>
public sealed class AgentPolicyRunner(
    AgentOptions options,
    AgentIdentity identity,
    PolicyJournal journal,
    PolicyCache cache,
    PolicyActionRunner actions,
    IPolicyProcess process,
    AgentInventoryHandler inventory,
    ILogger<AgentPolicyRunner> logger,
    AgentActivityTracker activityTracker)
{
    // Explicit requests bypass scheduled trigger selection; this value is local only.
    private const int RequestedTrigger = -1;

    private const int MaximumSummaryLength = 16_000;
    private const int MaximumActionDetailLength = 12_000;
    private const int MinimumStepLimit = 100;
    private const int StepLimitMultiplier = 4;
    private const int MaximumResponseBytes = 16 * 1024 * 1024;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    private bool recovered;
    private bool rebootScheduled;
    private readonly SemaphoreSlim gate = new(1, 1);
    private volatile bool loginAllowed;

    public bool LoginAllowed { get => loginAllowed; set => loginAllowed = value; }
    public bool WaitingForWindowsSetup { get; private set; }
    public bool DeploymentSetupPending { get; private set; }
    internal Func<AgentSetupReadiness> ReadSetupReadiness { get; set; } = WindowsSetupReadiness.Read;

    public Task RunAsync(int trigger, CancellationToken ct) => RunCoreAsync(trigger, null, ct);

    public Task RunLoginAsync(LoginPolicyContext login, CancellationToken ct) => RunCoreAsync(
        (int)PolicyTrigger.Login,
        login,
        ct);

    public Task RunRequestedAsync(LoginPolicyContext request, CancellationToken ct)
    {
        if (request.RequestedPolicyId is not > 0 || request.RequestId == Guid.Empty)
            throw new InvalidDataException("Invalid policy request.");

        return RunCoreAsync(RequestedTrigger, request, ct);
    }

    private async Task RunCoreAsync(int trigger, LoginPolicyContext? login, CancellationToken ct)
    {
        if (trigger == (int)PolicyTrigger.Login && login is null)
            throw new InvalidOperationException("Login policies require an authenticated user connection.");

        if (login is not null)
            await gate.WaitAsync(ct);
        else if (!await gate.WaitAsync(0, ct))
        {
            logger.LogInformation("Policy evaluation is already in progress; skipping duplicate request");

            return;
        }

        try
        {
            logger.LogInformation(
                $"Policy evaluation requested for trigger {trigger}; waiting for the local execution slot");
            await AgentExecutionGate.Gate.WaitAsync(ct);

            try
            {
                if (login is not null && !LoginAllowed)
                    throw new InvalidOperationException("Login policies are waiting for a matching agent version and successful check-in.");

                using var evaluationActivity = activityTracker.Begin(AgentActivityKind.EvaluatingPolicies);
                var deviceGuid = identity.State.DeviceGuid
                                 ?? throw new InvalidOperationException("Agent is not enrolled.");
                using var certificate = identity.GetClientCertificate()
                                        ?? throw new InvalidOperationException("Missing agent certificate.");
                using var handler = new HttpClientHandler { AllowAutoRedirect = false };
                AgentServerTrust.Configure(handler, options);
                handler.ClientCertificates.Add(certificate);
                using var client = new HttpClient(new SiteDownloadClient(new AgentResponseVerification(handler, options)))
                {
                    BaseAddress = options.GetServerUri(),
                    Timeout = RequestTimeout,
                    MaxResponseContentBufferSize = MaximumResponseBytes
                };

                if (!recovered)
                {
                    foreach (var run in journal.ReadActive(deviceGuid))
                        journal.Save(run with
                        {
                            State = ExecutionState.Interrupted,
                            Result = (int)PolicyRunResult.Failed,
                            FinishedAtUtc = DateTime.UtcNow,
                            Detail = "Agent stopped before recording completion. Execution outcome is uncertain; explicitly rerun the policy after review."
                        });

                    recovered = true;
                }

                await ReportAsync(client, deviceGuid, ct);
                var readiness = rebootScheduled
                    ? new AgentSetupReadiness(false, "Waiting for the policy-requested restart.")
                    : ReadSetupReadiness();
                WaitingForWindowsSetup = !readiness.Ready;
                using (var response = await client.PostAsJsonAsync("api/agent/v1/policies/setup-readiness", readiness, ct))
                {
                    response.EnsureSuccessStatusCode();
                    DeploymentSetupPending = (await response.Content.ReadFromJsonAsync<AgentSetupReadinessReceipt>(ct)
                        ?? throw new InvalidDataException("Missing setup readiness receipt.")).DeploymentPending;
                }

                if (!readiness.Ready)
                {
                    logger.LogInformation($"Policies paused: {readiness.Reason}");
                    if (login?.RequestedPolicyId is not null)
                        throw new InvalidOperationException("Policies are waiting for Windows setup to finish.");
                    return;
                }

                logger.LogInformation("Retrieving assigned policies from the server");
                var manual = login?.RequestedPolicyId is not null;

                if (manual && journal.Contains(login!.RequestId))
                    throw new InvalidOperationException("This request has already been handled; review its result before running again.");

                var endpoint = login is null
                    ? "api/agent/v1/policies"
                    : $"api/agent/v1/policies/user?sid={Uri.EscapeDataString(login.Sid)}&selfService={manual}";
                var snapshots = await client.GetFromJsonAsync<List<AgentPolicySnapshot>>(endpoint, ct) ?? [];
                var policies = snapshots.Select(x => (Snapshot: x, Policy: PolicySelection.Validate(x)))
                    .OrderBy(x => x.Policy.ExecutionPriority).ThenBy(x => x.Policy.Id).ToList();
                logger.LogInformation($"Found {policies.Count} assigned policies");

                if (manual)
                {
                    policies = policies.Where(x => x.Policy.Id == login!.RequestedPolicyId).ToList();

                    if (policies.Count != 1)
                        throw new UnauthorizedAccessException("Policy is no longer available to this user.");
                }

                if (!manual && login is not null && !journal.ClaimLogin(deviceGuid, login.LogonKey))
                    return;

                var started = 0;
                var reboot = false;
                var rebootIfNoLogins = false;
                foreach (var (snapshot, policy) in policies)
                {
                    ct.ThrowIfCancellationRequested();

                    if (!manual
                        && !PolicySelection.IsDue(
                            snapshot,
                            policy,
                            trigger,
                            login is null
                                ? journal.ReadHistory(deviceGuid, policy.Guid, snapshot.Hash)
                                : journal.ReadLoginHistory(deviceGuid, policy.Guid, snapshot.Hash, login.Sid),
                            DateTime.UtcNow))
                    {
                        logger.LogInformation(
                            $"Skipping policy {policy.Name} ({policy.Guid}): not due for this trigger, schedule or execution history");
                        continue;
                    }

                    started++;
                    var run = new PolicyRunEntry(
                        manual ? login!.RequestId : Guid.NewGuid(),
                        deviceGuid,
                        snapshot,
                        ExecutionState.Prepared,
                        DateTime.UtcNow,
                        null,
                        null,
                        null,
                        false,
                        login?.UserName)
                    {
                        UserSid = login?.Sid,
                        SelfService = manual
                    };
                    journal.Save(run);

                    string[]? notificationRecipients = null;
                    try
                    {
                        using var response = await client.PostAsJsonAsync(
                            "api/agent/v1/policies/runs",
                            new AgentPolicyStart(run.RunId, policy.Id, snapshot.Hash, run.UserName)
                            {
                                UserSid = run.UserSid,
                                SelfService = run.SelfService, SupportsNotificationRecipients = true
                            },
                            ct);
                        response.EnsureSuccessStatusCode();
                        var receipt = await response.Content.ReadFromJsonAsync<AgentPolicyReceipt>(ct)
                                      ?? throw new InvalidDataException("Missing policy receipt.");

                        notificationRecipients = receipt.NotificationRecipientSids;
                        if (receipt.RunId != run.RunId || receipt.Completed)
                            throw new InvalidDataException("Unexpected policy admission receipt.");
                    }
                    catch
                    {
                        // Admission may have committed even if its HTTP response was lost, but no local effect has started.
                        journal.Save(run with
                        {
                            State = ExecutionState.Completed,
                            Result = (int)PolicyRunResult.Failed,
                            FinishedAtUtc = DateTime.UtcNow,
                            Detail = "Policy admission failed before execution."
                        });
                        throw;
                    }

                    run = run with
                    {
                        State = ExecutionState.Running
                    };
                    journal.Save(run); // Durable before the first possible effect.
                    using var policyActivity = activityTracker.Begin(AgentActivityKind.RunningPolicy, run.RunId);
                    logger.LogInformation($"Running policy {policy.Name} ({policy.Guid})");
                    var detail = new StringBuilder();
                    var result = (int)PolicyRunResult.Failed;
                    var interrupted = false;

                    try
                    {
                        if (policy.Settings.Number("ExecutionType") == (int)PolicyExecutionType.Install)
                            PolicyActionRunner.Validate(policy);

                        var userExecution = PolicyUserContext.UsesUserContext(policy.Settings)
                            ? login
                              ?? throw new InvalidOperationException("User execution requires an authenticated user session.")
                            : null;

                        if (userExecution is not null)
                            PolicyUserContext.Validate(policy);

                        logger.LogInformation($"Policy {policy.Guid}: preparing files");
                        await cache.DownloadAsync(client, run, policy, ct, null);

                        if (policy.Settings.Number("ExecutionType") == (int)PolicyExecutionType.Cache)
                        {
                            result = (int)PolicyRunResult.Success;
                            detail.AppendLine("Policy files cached; actions were not executed.");
                        }
                        else
                            result = await ExecuteAsync(client, run, policy, detail, ct, userExecution, notificationRecipients);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        journal.Save(run with
                        {
                            State = ExecutionState.Interrupted,
                            Result = (int)PolicyRunResult.Failed,
                            FinishedAtUtc = DateTime.UtcNow,
                            Detail = "Policy execution was interrupted. Review the device before explicitly rerunning this policy."
                        });
                        throw;
                    }
                    catch (OperationCanceledException)
                    {
                        interrupted = true;
                        detail.AppendLine("Execution timed out; its outcome may be uncertain. Review before explicitly rerunning.");
                    }
                    catch (Exception ex)
                    {
                        // Do not send command arguments, passwords, or exception stack traces to history.
                        detail.AppendLine(ex is NotSupportedException or InvalidDataException
                            ? ex.Message
                            : $"Policy execution failed: {ex.GetType().Name}");
                        logger.LogWarning($"Policy {policy.Guid} failed ({ex.GetType().Name})");
                    }

                    var summary = detail.ToString();
                    run = run with
                    {
                        State = interrupted ? ExecutionState.Interrupted : ExecutionState.Completed,
                        Result = result,
                        FinishedAtUtc = DateTime.UtcNow,
                        Detail = summary[..Math.Min(summary.Length, MaximumSummaryLength)]
                    };
                    journal.Save(run);

                    if (manual)
                        login!.Result = result;

                    logger.LogInformation($"Policy {policy.Guid}: finished with result {result}");
                    await ReportAsync(client, deviceGuid, ct);

                    if (result == (int)PolicyRunResult.Success
                        && policy.Settings.Number("ExecutionType") == (int)PolicyExecutionType.Install)
                    {
                        if (policy.Settings.Flag("RemoveInstallCache"))
                        {
                            try
                            {
                                cache.Remove(snapshot);
                            }
                            catch (Exception ex)
                            {
                                logger.LogWarning($"Could not remove policy cache ({ex.GetType().Name})");
                            }
                        }

                        reboot |= policy.Settings.Number("CompletedAction") == (int)PolicyCompletedAction.Reboot;
                        rebootIfNoLogins |= policy.Settings.Number("CompletedAction") == (int)PolicyCompletedAction.RebootIfNoLogins;
                    }

                    if (interrupted
                        || (result == (int)PolicyRunResult.Failed
                            && policy.Settings.Number("ErrorAction") == (int)PolicyErrorAction.AbortRemainingPolicies))
                        break;
                }

                if (started == 0)
                    logger.LogInformation("No policies are due; nothing to execute");
                else
                    logger.LogInformation($"Policy evaluation finished; attempted {started} policies");

                if (reboot || (rebootIfNoLogins && !WindowsLoggedInUsers.HasLoggedOnUsers()))
                {
                    var result = await process.RunAsync(
                        new ProcessStartInfo(
                            PolicyActionRunner.WindowsTool("shutdown.exe"),
                            "/r /t 60 /c \"CloneDeploy policy requires a restart.\""),
                        1,
                        ct);

                    if (result.ExitCode != 0)
                        logger.LogError($"Policy restart could not be scheduled; exit code {result.ExitCode}");
                    else
                        rebootScheduled = true;
                }
            }
            finally
            {
                AgentExecutionGate.Gate.Release();
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task ReportAsync(HttpClient client, Guid deviceGuid, CancellationToken ct)
    {
        foreach (var (admission, result) in journal.ReadReportPayloads(deviceGuid))
        {
            try
            {
                // Retry admission with the same ID if its response was lost. No action is executed here.
                using var start = await client.PostAsJsonAsync("api/agent/v1/policies/runs", admission, ct);

                if (start.StatusCode == HttpStatusCode.NotFound)
                {
                    journal.MarkReported(admission.RunId);
                    logger.LogWarning($"Policy {admission.RunId} was removed before its result could be accepted");
                    continue;
                }

                start.EnsureSuccessStatusCode();
                using var response = await client.PostAsJsonAsync("api/agent/v1/policies/results", result, ct);
                response.EnsureSuccessStatusCode();
                journal.MarkReported(admission.RunId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning($"Policy result {admission.RunId} remains queued ({ex.GetType().Name})");
            }
        }
    }

    private async Task<int> ExecuteAsync(
        HttpClient client,
        PolicyRunEntry run,
        AgentPolicyContent policy,
        StringBuilder detail,
        CancellationToken ct,
        LoginPolicyContext? login, string[]? notificationRecipients)
    {
        async Task<bool> RunAction(AgentPolicyAction action)
        {
            await using var slot = (policy.Settings.Flag("WinGetUseMaxConnections")
                                    || action.Settings.Flag("UseDownloadQueue"))
                                   && action.Type == (int)ActionType.Software
                                   && action.Settings.Number("Type") == (int)SoftwareActionType.WinGet
                ? await PolicyDownloadSlot.AcquireAsync(client, run.RunId, ct, null)
                : null;
            logger.LogInformation($"Policy {policy.Guid}: running {action.Name}");

            if ((login is not null || notificationRecipients is not null) && action.Type == (int)ActionType.Notification)
            {
                var settings = action.Settings;
                (actions.Notifications ?? throw new InvalidOperationException("Portal messages are not configured.")).StoreForRecipients(
                    PortalNotice.Create(settings, run.RunId, action.Guid),
                    notificationRecipients, login?.Sid);
                detail.AppendLine($"{action.Name}: Notification queued for eligible recipients; acknowledgment is tracked separately.");

                return true;
            }

            var blocked = login is not null && WinGetFeatureGate.Applies(action)
                ? await WinGetFeatureGate.CheckAsync(client, ct) : null;
            var result = blocked ?? (login is null
                ? await actions.RunAsync(
                    action,
                    cache.DirectoryFor(run.Snapshot, action),
                    client,
                    run.RunId,
                    ct,
                    null)
                : await login.Execute(action, cache.DirectoryFor(run.Snapshot, action), ct));
            var actionDetail = ActionExecutionLog.Format(
                action,
                result,
                policy.Settings.Number("LogLevel") == (int)PolicyLogLevel.Full);
            logger.Log(
                PolicyActionRunner.Succeeded(action, result) ? LogLevel.Information : LogLevel.Warning,
                $"Policy {policy.Guid}: {actionDetail}");

            if (detail.Length < MaximumActionDetailLength)
                detail.AppendLine(actionDetail);

            return PolicyActionRunner.Succeeded(action, result);
        }

        static int ConditionResult(int behavior) => behavior switch
        {
            (int)PolicyConditionFailedAction.MarkNotApplicable => (int)PolicyRunResult.NotApplicable,
            (int)PolicyConditionFailedAction.MarkSkipped => (int)PolicyRunResult.Skipped,
            (int)PolicyConditionFailedAction.MarkSuccess => (int)PolicyRunResult.Success,
            _ => (int)PolicyRunResult.Failed
        };

        if (policy.Condition is { } condition && !await RunAction(condition))
            return ConditionResult(policy.Settings.Number("ConditionFailedAction"));

        async Task CollectInventory()
        {
            var receipt = await inventory.CollectAsync(
                new AgentInventoryRequest(Guid.NewGuid(), run.DeviceGuid, DateTimeOffset.UtcNow.AddMinutes(5)),
                run.DeviceGuid,
                (collected, token) => CustomInventoryCollector.CollectAsync(
                    policy,
                    run.RunId,
                    collected,
                    (script, scriptToken) => actions.RunAsync(
                        script, cache.DirectoryFor(run.Snapshot, script), client, run.RunId, scriptToken, null),
                    token),
                ct);
            detail.AppendLine(receipt.Detail);

            if (!receipt.Saved)
                throw new InvalidOperationException("Inventory failed.");
        }

        var settings = policy.Settings;
        // Numeric timing values remain supported in previously published snapshots.
        if (settings.Number("RunInventory") is (int)PolicyInventoryAction.Before or (int)PolicyInventoryAction.Both)
            await CollectInventory();

        var ordered = policy.Actions.Where(x => !x.Settings.Flag("AddInventoryCollection"))
            .OrderBy(x => x.Order).ThenBy(x => x.Guid).ToList();
        var failed = false;
        var steps = 0;

        for (var index = 0; index < ordered.Count; index++)
        {
            if (++steps > Math.Max(MinimumStepLimit, ordered.Count * StepLimitMultiplier))
                throw new InvalidDataException("Policy condition jump loop detected.");

            ct.ThrowIfCancellationRequested();
            var action = ordered[index];

            if (action.Condition is { } actionCondition && !await RunAction(actionCondition))
            {
                if (action.ConditionFailedAction != (int)PolicyConditionFailedAction.GotoModule)
                    return ConditionResult(action.ConditionFailedAction);

                var target = ordered.FindIndex(x => x.Guid == action.ConditionNextAction);

                if (target < 0)
                    throw new InvalidDataException("Policy jump target was not found.");

                index = target - 1;
                continue;
            }

            try
            {
                if (await RunAction(action))
                    continue;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                detail.AppendLine($"{action.Name}: {ex.GetType().Name}");
            }

            failed = true;

            if (settings.Number("ErrorAction") != (int)PolicyErrorAction.Continue)
                break;
        }

        if (settings.Flag("RunInventory")
            || settings.Number("RunInventory") is (int)PolicyInventoryAction.After or (int)PolicyInventoryAction.Both)
            await CollectInventory();

        if (settings.Number("LogLevel") == (int)PolicyLogLevel.None)
        {
            detail.Clear();
            detail.AppendLine(failed ? "Policy failed." : "Policy completed.");
        }

        return failed ? (int)PolicyRunResult.Failed : (int)PolicyRunResult.Success;
    }
}
