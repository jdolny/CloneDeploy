using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent.MacOS;

/// <summary>
/// Picks up deployment work from the server, runs it, and reports the outcome.
/// </summary>
public sealed class MacTaskRunner(MacConfiguration configuration, MacIdentity identity, string directory,
    PortalNotificationJournal notifications, SemaphoreSlim executionGate, MacActivityTracker activityTracker)
{
    private readonly DeviceTaskJournal journal = new(configuration.Options(directory));
    public IMacScriptRunner Scripts { get; init; } = new MacScriptRunner(directory);
    public IMacScriptRunner Commands { get; init; } = new MacCommandRunner(directory);
    public MacPackageDeployment Packages { get; init; } = new(configuration.Options(directory));
    private const int MaximumTasksPerCycle = 100;
    private const int MaximumDetailLength = 4000;
    private const int MaximumResponseBytes = 16 * 1024 * 1024;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    public async Task<bool> RunAsync(bool regularCheckin, CancellationToken ct)
    {
        Console.WriteLine(
            $"Checking device tasks ({(regularCheckin ? "regular check-in" : "server notification or retry")}); waiting for the local execution slot");
        await executionGate.WaitAsync(ct);

        try
        {
            var deviceGuid = identity.State.DeviceGuid ?? throw new InvalidOperationException("Agent is not enrolled.");
            using var certificate = identity.GetClientCertificate()
                                    ?? throw new InvalidOperationException("Missing certificate.");
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            var options = configuration.Options(directory);
            AgentServerTrust.Configure(handler, options);
            handler.ClientCertificates.Add(certificate);
            using var client = new HttpClient(new AgentResponseVerification(handler, options))
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
                    Console.WriteLine("Server task capacity is busy; queued tasks will be retried");

                    return true;
                }

                if (response.StatusCode == HttpStatusCode.NoContent)
                {
                    Console.WriteLine("No eligible device task offered; nothing to execute now");

                    return false;
                }

                response.EnsureSuccessStatusCode();
                var offer = await response.Content.ReadFromJsonAsync<AgentTaskOffer>(ct)
                            ?? throw new InvalidDataException("Missing task offer.");

                if (offer.TaskId == Guid.Empty || offer.LeaseToken == Guid.Empty)
                    throw new InvalidDataException("Invalid task identity.");

                Console.WriteLine($"Server offered device task {offer.TaskId}; requesting permission to start");
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
                    Console.WriteLine(
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

                    if (action.Guid == Guid.Empty || action.Condition is not null || action.Settings.Flag("AddInventoryCollection") || action.Settings.Flag("IsCondition"))
                        throw new InvalidDataException("Invalid standalone action.");

                    execution.Token.ThrowIfCancellationRequested();
                    if (action.Type == (int)ActionType.File || MacPackageDeployment.Supports(action))
                    {
                        client.DefaultRequestHeaders.Add("X-Task-Lease", run.LeaseToken.ToString());
                        try
                        {
                            var result = action.Type == (int)ActionType.File
                                ? await new MacFileDeployment(options).RunAsync(action, client, run.TaskId, deviceGuid,
                                    hash.ToLowerInvariant(), $"api/agent/v1/tasks/{run.TaskId}", execution.Token)
                                : await Packages.RunAsync(action, client, run.TaskId, deviceGuid,
                                    hash.ToLowerInvariant(), $"api/agent/v1/tasks/{run.TaskId}", execution.Token);
                            success = result.Success;
                            detail = result.Detail;
                        }
                        finally { client.DefaultRequestHeaders.Remove("X-Task-Lease"); }
                    }
                    else if (MacScriptRunner.Supports(action) || action.Type == (int)ActionType.Command)
                    {
                        var result = await (action.Type == (int)ActionType.Command ? Commands : Scripts).RunAsync(action, execution.Token);
                        success = result.Success;
                        detail = result.Detail;
                    }
                    else if (action.Type == (int)ActionType.Notification)
                    {
                    notifications.Store(PortalNotice.Create(action.Settings, run.TaskId, action.Guid));
                    success = true;
                    detail = $"Notification queued: {action.Name}. Delivery does not confirm that a user has read it.";
                    }
                    else throw new NotSupportedException($"Action '{action.Name}' (type {action.Type}) is not supported by the macOS agent.");
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
                    Console.WriteLine($"Device task {run.TaskId} failed ({ex.GetType().Name})");
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
                Console.WriteLine(
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
            executionGate.Release();
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
                    Console.WriteLine(
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
                Console.WriteLine($"Task {run.TaskId} was resolved or its lease was replaced before reporting");
            else
                result.EnsureSuccessStatusCode();

            journal.Save(run with
            {
                Reported = true
            });
            Console.WriteLine($"Task {run.TaskId}: result reporting finished");
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
            Console.WriteLine($"Task heartbeat {run.TaskId} failed ({ex.GetType().Name}); stopping execution");
            execution.Cancel();
        }
    }
}
