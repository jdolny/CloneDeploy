using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent;

/// <summary>
/// Keeps the agent checking in and starts policy and deployment work when the server accepts it.
/// </summary>
public class Worker(
    AgentTaskRunner tasks,
    AgentPolicyRunner policies,
    AgentClient client,
    AgentRealtimeClient realtime,
    AgentOptions options,
    IHostApplicationLifetime lifetime,
    ILogger<Worker> logger,
    AgentActivityTracker activityTracker) : BackgroundService
{
    private const int CheckInFailureRetrySeconds = 60;
    private const int CheckInRetryJitterSeconds = 15;
    private const int DeferredUpdateRetrySeconds = 60;
    private static readonly TimeSpan PolicyCheckInWait = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PolicyExecutionPollInterval = TimeSpan.FromSeconds(5);

    public AgentUpdater? Updater { get; set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var startup = true;

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(CheckInFailureRetrySeconds + Random.Shared.Next(CheckInRetryJitterSeconds));

            try
            {
                logger.LogInformation("Starting check-in cycle with the server");
                AgentCycleResult result;
                using (activityTracker.Begin(CloneDeploy_Agent.Contracts.AgentActivityKind.CheckingIn))
                    result = await client.RunOnceAsync(stoppingToken);
                policies.LoginAllowed = result.CanRunPolicies(typeof(Worker).Assembly.GetName().Version!.ToString());
                logger.LogInformation(
                    $"Agent {result.Status}; device {result.DeviceGuid}; next attempt in {result.RetryAfter.TotalSeconds}s");
                delay = result.RetryAfter;
                if (result.EnrollmentWaitReason is { } reason)
                    logger.LogInformation("Enrollment deferred: {Reason}", reason);

                if (result.Status == AgentCycleStatus.CheckedIn)
                {
                    if (Updater is not null)
                        await Updater.CheckAsync(result.Update, stoppingToken);

                    var installedVersion = typeof(Worker).Assembly.GetName().Version!.ToString();

                    if (!options.RunOnce)
                        realtime.TaskSignal?.Request(true);

                    if (result.CanRunPolicies(installedVersion))
                    {
                        logger.LogInformation("Check-in accepted and agent version matches; checking assigned policies");
                        var execution = policies.RunAsync(
                            startup ? (int)PolicyTrigger.Startup : (int)PolicyTrigger.Checkin,
                            stoppingToken);

                        try
                        {
                            while (!execution.IsCompleted)
                            {
                                await realtime.WaitForCheckInAsync(PolicyCheckInWait, stoppingToken);
                                await Task.WhenAny(execution, Task.Delay(PolicyExecutionPollInterval, stoppingToken));
                            }
                        }
                        finally
                        {
                            await execution;
                            if (policies.WaitingForWindowsSetup || policies.DeploymentSetupPending)
                                delay = TimeSpan.FromSeconds(30);
                        }

                        if (!policies.WaitingForWindowsSetup)
                            startup = false;
                    }
                    else
                    {
                        logger.LogWarning(
                            $"Policies paused: installed agent {installedVersion} must match prepared release {result.Update!.Version}. Check-ins and update attempts will continue");
                        // Recheck promptly after a deferred upgrade instead of waiting the normal hourly interval.
                        delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds, DeferredUpdateRetrySeconds));
                    }

                    if (options.RunOnce)
                    {
                        // RunOnce must finish direct tasks before stopping the host.
                        while (await tasks.RunAsync(true, stoppingToken))
                            await Task.Delay(
                                TimeSpan.FromSeconds(Random.Shared.Next(
                                    TaskRetry.MinimumSeconds,
                                    TaskRetry.MaximumSeconds + 1)),
                                stoppingToken);
                    }
                }

                if (options.RunOnce)
                {
                    Environment.ExitCode = result.CanRunPolicies(typeof(Worker).Assembly.GetName().Version!.ToString())
                        && !policies.WaitingForWindowsSetup
                        ? ProcessExitCodes.Success
                        : ProcessExitCodes.PoliciesUnavailable;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
            {
                policies.LoginAllowed = false;
                var reason = exception is HttpRequestException request
                    ? $"{request.HttpRequestError}: {request.GetBaseException().Message}"
                    : "Connection timed out";
                // Keep the inner TLS/socket error visible at the normal log level.
                logger.LogWarning(exception, $"Agent server request failed; will retry. {reason}");

                if (options.RunOnce)
                {
                    Environment.ExitCode = ProcessExitCodes.Failure;
                }
            }
            catch (Exception exception)
            {
                policies.LoginAllowed = false;
                logger.LogError(exception, "Agent attempt failed.");

                if (options.RunOnce)
                {
                    Environment.ExitCode = ProcessExitCodes.Failure;
                }
            }

            if (options.RunOnce)
            {
                lifetime.StopApplication();
                break;
            }

            logger.LogInformation(
                $"Waiting {Math.Round(delay.TotalSeconds)}s before the next check-in. Listening for server requests...");
            await realtime.WaitForNextCheckInAsync(delay, stoppingToken);
        }
    }
}
