using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent;

/// <summary>
/// Picks up deployment work from the server, runs it, and reports the outcome.
/// </summary>
public sealed class AgentTaskRunner(
    AgentOptions options,
    AgentIdentity identity,
    DeviceTaskJournal journal,
    PolicyCache cache,
    PolicyActionRunner actions,
    ILogger<AgentTaskRunner> logger,
    AgentActivityTracker activityTracker)
{
    private const int MaximumTasksPerCycle = 100;
    private const int MaximumDetailLength = 4000;
    private const int MaximumResponseBytes = 16 * 1024 * 1024;
    private const string TaskLeaseHeader = "X-Task-Lease";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    public async Task<bool> RunAsync(bool regularCheckin, CancellationToken ct)
    {
        logger.LogInformation(
            $"Checking device tasks ({(regularCheckin ? "regular check-in" : "server notification or retry")}); waiting for the local execution slot");
        await AgentExecutionGate.Gate.WaitAsync(ct);

        try
        {
            var deviceGuid = identity.State.DeviceGuid ?? throw new InvalidOperationException("Agent is not enrolled.");
            using var certificate = identity.GetClientCertificate()
                                    ?? throw new InvalidOperationException("Missing certificate.");
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            AgentServerTrust.Configure(handler, options);
            handler.ClientCertificates.Add(certificate);
            using var client = new HttpClient(new SiteDownloadClient(new AgentResponseVerification(handler, options)))
            {
                BaseAddress = options.GetServerUri(),
                Timeout = RequestTimeout,
                MaxResponseContentBufferSize = MaximumResponseBytes
            };
            await RecoverAndReportAsync(client, deviceGuid, ct);

            for (var i = 0; i < MaximumTasksPerCycle; i++)
            {
                using var response = await client.PostAsJsonAsync(
                    "api/agent/v1/tasks/claim",
                    new AgentTaskClaim(regularCheckin),
                    ct);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    logger.LogInformation("Server task capacity is busy; queued tasks will be retried");

                    return true;
                }

                if (response.StatusCode == HttpStatusCode.NoContent)
                {
                    logger.LogInformation("No eligible device task offered; nothing to execute now");

                    return false;
                }

                response.EnsureSuccessStatusCode();
                var offer = await response.Content.ReadFromJsonAsync<AgentTaskOffer>(ct)
                            ?? throw new InvalidDataException("Missing task offer.");

                if (offer.TaskId == Guid.Empty || offer.LeaseToken == Guid.Empty)
                    throw new InvalidDataException("Invalid task identity.");

                logger.LogInformation($"Server offered device task {offer.TaskId}; requesting permission to start");
                var run = new DeviceTaskRun(
                    offer.TaskId,
                    offer.LeaseToken,
                    deviceGuid,
                    ExecutionState.Prepared,
                    DateTime.UtcNow);
                // Persist before admission; a lost start response can be reconciled without repeating effects.
                if (journal.HasExecution(offer.TaskId))
                    throw new InvalidDataException("Server reoffered a task already executed locally.");

                journal.Save(run);
                using var start = await client.PostAsJsonAsync(
                    $"api/agent/v1/tasks/{offer.TaskId}/start",
                    new AgentTaskLease(offer.LeaseToken),
                    ct);

                if (start.StatusCode == HttpStatusCode.Conflict)
                {
                    logger.LogInformation(
                        $"Task {run.TaskId}: start was declined; the offer may have expired or been cancelled");
                    journal.Save(run with
                    {
                        State = ExecutionState.Abandoned,
                        Reported = true
                    });
                    continue;
                }

                start.EnsureSuccessStatusCode();
                run = run with
                {
                    State = ExecutionState.Running
                };
                journal.Save(run);
                using var taskActivity = activityTracker.Begin(AgentActivityKind.RunningTask, run.TaskId);
                using var execution = CancellationTokenSource.CreateLinkedTokenSource(ct);
                using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var heartbeat = HeartbeatAsync(client, run, execution, heartbeatStop.Token);
                var success = false;
                var interrupted = false;
                var detail = "";

                try
                {
                    using var envelope = JsonDocument.Parse(offer.ActionJson);
                    var raw = envelope.RootElement.GetProperty("Action");
                    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw.GetRawText())));

                    if (envelope.RootElement.GetProperty("SchemaVersion").GetInt32() != 1
                        || !hash.Equals(offer.ContentHash, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Task snapshot version or SHA-256 is invalid.");

                    var action = raw.Deserialize<AgentPolicyAction>()
                                 ?? throw new InvalidDataException("Missing action.");

                    if (action.Guid == Guid.Empty || action.Condition is not null)
                        throw new InvalidDataException("Invalid standalone action.");

                    var policy = new AgentPolicyContent(
                        1,
                        action.Guid,
                        action.Name,
                        0,
                        JsonSerializer.SerializeToElement(new { }),
                        null,
                        null,
                        null,
                        [action]);
                    PolicyActionRunner.Validate(policy);
                    // This local envelope only reuses the verified file cache; no policy is created on the server.
                    var snapshot = new AgentPolicySnapshot(
                        1,
                        hash.ToLowerInvariant(),
                        false,
                        JsonSerializer.SerializeToElement(policy));
                    var cacheRun = new PolicyRunEntry(
                        run.TaskId,
                        deviceGuid,
                        snapshot,
                        ExecutionState.Running,
                        run.StartedAtUtc,
                        null,
                        null,
                        null,
                        false);
                    var contentPath = $"api/agent/v1/tasks/{run.TaskId}";
                    client.DefaultRequestHeaders.Remove(TaskLeaseHeader);
                    client.DefaultRequestHeaders.Add(TaskLeaseHeader, run.LeaseToken.ToString());
                    logger.LogInformation($"Task {run.TaskId}: preparing files for {action.Name}");
                    await cache.DownloadAsync(client, cacheRun, policy, execution.Token, contentPath);
                    await using var slot = action.Type == (int)ActionType.Software
                                           && action.Settings.Number("Type") == (int)SoftwareActionType.WinGet
                                           && action.Settings.Number("WinGetInstallType") != (int)WinGetInstallType.Uninstall
                                           && (!action.Settings.Flag("ExplicitWinGetOperation")
                                               || action.Settings.Flag("UseDownloadQueue"))
                        ? await PolicyDownloadSlot.AcquireAsync(
                            client,
                            run.TaskId,
                            execution.Token,
                            contentPath)
                        : null;
                    logger.LogInformation($"Task {run.TaskId}: running {action.Name}");
                    var result = await actions.RunAsync(
                        action,
                        cache.DirectoryFor(snapshot, action),
                        client,
                        run.TaskId,
                        execution.Token,
                        contentPath);
                    success = PolicyActionRunner.Succeeded(action, result);
                    detail = ActionExecutionLog.Format(action, result);
                }
                catch (OperationCanceledException)
                {
                    interrupted = true;
                    detail = "Execution was interrupted or its lease could not be maintained. Review the device before resolving this task.";
                }
                catch (Exception ex)
                {
                    detail = ex is InvalidDataException or NotSupportedException
                        ? ex.Message
                        : $"Action failed: {ex.GetType().Name}";
                    logger.LogWarning($"Device task {run.TaskId} failed ({ex.GetType().Name})");
                }
                finally
                {
                    heartbeatStop.Cancel();
                    await heartbeat;
                }

                run = run with
                {
                    State = interrupted ? ExecutionState.Interrupted : ExecutionState.Completed,
                    FinishedAtUtc = DateTime.UtcNow,
                    Success = success,
                    Detail = detail[..Math.Min(detail.Length, MaximumDetailLength)]
                };
                journal.Save(run);
                logger.Log(success ? LogLevel.Information : LogLevel.Warning,
                    $"Task {run.TaskId} {run.State}: {run.Detail}");
                ct.ThrowIfCancellationRequested();
                await RecoverAndReportAsync(client, deviceGuid, ct);

                if (interrupted)
                    return false;
            }

            return true;
        }
        finally
        {
            AgentExecutionGate.Gate.Release();
        }
    }

    private async Task RecoverAndReportAsync(HttpClient client, Guid deviceGuid, CancellationToken ct)
    {
        foreach (var stored in journal.Pending(deviceGuid))
        {
            var run = stored;

            if (run.State == ExecutionState.Prepared)
            {
                using var start = await client.PostAsJsonAsync(
                    $"api/agent/v1/tasks/{run.TaskId}/start",
                    new AgentTaskLease(run.LeaseToken),
                    ct);

                if (start.StatusCode == HttpStatusCode.Conflict)
                {
                    logger.LogInformation(
                        $"Task {run.TaskId}: start was declined; the offer may have expired or been cancelled");
                    journal.Save(run with
                    {
                        State = ExecutionState.Abandoned,
                        Reported = true
                    });
                    continue;
                }

                start.EnsureSuccessStatusCode();
                run = run with
                {
                    State = ExecutionState.Completed,
                    Success = false,
                    FinishedAtUtc = DateTime.UtcNow,
                    Detail = "Agent stopped during admission, before beginning execution. Submit a new deployment to retry."
                };
                journal.Save(run);
            }

            if (run.State == ExecutionState.Running)
            {
                run = run with
                {
                    State = ExecutionState.Interrupted,
                    Success = false,
                    FinishedAtUtc = DateTime.UtcNow,
                    Detail = "Agent stopped before recording completion. Execution outcome is uncertain; review the device before resolving this task."
                };
                journal.Save(run);
            }

            using var result = await client.PostAsJsonAsync(
                $"api/agent/v1/tasks/{run.TaskId}/result",
                new AgentTaskResult(
                    run.LeaseToken,
                    run.Success == true,
                    run.Detail ?? "",
                    run.State == ExecutionState.Interrupted),
                ct);

            if (result.StatusCode == HttpStatusCode.Conflict)
                logger.LogWarning($"Task {run.TaskId} was resolved or its lease was replaced before reporting");
            else
                result.EnsureSuccessStatusCode();

            journal.Save(run with
            {
                Reported = true
            });
            logger.LogInformation($"Task {run.TaskId}: result reporting finished");
        }
    }

    private async Task HeartbeatAsync(
        HttpClient client,
        DeviceTaskRun run,
        CancellationTokenSource execution,
        CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(HeartbeatInterval, ct);
                using var response = await client.PostAsJsonAsync(
                    $"api/agent/v1/tasks/{run.TaskId}/heartbeat",
                    new AgentTaskLease(run.LeaseToken),
                    ct);
                response.EnsureSuccessStatusCode();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning($"Task heartbeat {run.TaskId} failed ({ex.GetType().Name}); stopping execution");
            execution.Cancel();
        }
    }
}
