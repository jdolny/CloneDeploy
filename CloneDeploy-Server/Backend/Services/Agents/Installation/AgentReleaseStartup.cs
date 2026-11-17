using System.Security.Claims;

namespace CloneDeploy_Server.Backend.Services;

// An in-process capability used only for preparing releases. It is never issued by authentication.
internal sealed class AgentReleaseStartupPrincipal : ClaimsPrincipal
{
    internal static readonly AgentReleaseStartupPrincipal Instance = new();
    private AgentReleaseStartupPrincipal() : base(new ClaimsIdentity([
        new Claim(ClaimTypes.NameIdentifier, "0"), new Claim(ClaimTypes.Name, "Agent release startup")])) { }
    internal static bool IsStartup(ClaimsPrincipal user) => ReferenceEquals(user, Instance);
}

public sealed class AgentReleaseStartup(IServiceScopeFactory scopes, IHostApplicationLifetime lifetime,
    ILogger<AgentReleaseStartup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
            await started.Task.WaitAsync(stoppingToken);
            foreach (var architecture in new[] { "x64", "arm64", "mac" })
            {
                stoppingToken.ThrowIfCancellationRequested();
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var prepared = architecture == "mac"
                        ? await scope.ServiceProvider.GetRequiredService<MacAgentReleaseService>().PrepareAtStartupAsync(stoppingToken)
                        : await scope.ServiceProvider.GetRequiredService<AgentReleaseService>().PrepareAtStartupAsync(architecture, stoppingToken);
                    logger.LogInformation("Agent release startup preparation for {Architecture}: {Result}", architecture, prepared ? "prepared successfully" : "already current or newer; skipped");
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (InvalidDataException ex)
                {
                    logger.LogWarning("Agent release startup preparation for {Architecture} was not completed: {Reason}. Use Prepare agent release after correcting the configuration.", architecture, ex.Message);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Agent release startup preparation failed for {Architecture}. Use Prepare agent release to retry.", architecture);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Agent release startup preparation cancelled during shutdown.");
        }
    }
}
