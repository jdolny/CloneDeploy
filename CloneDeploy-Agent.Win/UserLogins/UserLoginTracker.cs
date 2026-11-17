namespace CloneDeploy_Agent;

/// <summary>
/// Regularly checks who is signed in and records when users appear or leave.
/// </summary>
public sealed class UserLoginTracker(UserLoginJournal journal, ILogger<UserLoginTracker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private readonly object gate = new();

    public void Sample()
    {
        lock (gate)
        {
            try
            {
                journal.Observe(WindowsLoggedInUsers.Read(), DateTimeOffset.UtcNow);
            }
            catch (Exception ex)
            {
                // A failed enumeration is not evidence that everyone logged out.
                logger.LogWarning(ex, "Could not sample user login history; retaining previous observations");
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            Sample();
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}