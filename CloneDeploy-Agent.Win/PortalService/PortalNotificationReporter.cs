namespace CloneDeploy_Agent;

/// <summary>Uploads changed receipts and checks pending notices for acknowledgments made elsewhere. Offline acknowledgments stay queued in SQLite.</summary>
public sealed class PortalNotificationReporter(
    PortalNotificationJournal journal,
    AgentClient client,
    ILogger<PortalNotificationReporter> logger) : BackgroundService
{
    private static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var reports = journal.PendingReports();

                if (reports.Length > 0)
                {
                    var acknowledgments = await client.ReportNotificationsAsync(reports, stoppingToken);

                    foreach (var report in reports)
                        journal.MarkReported(report);
                    journal.ApplyAcknowledgments(acknowledgments);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Message receipts remain queued for reporting");
            }

            try
            {
                await Task.Delay(ReportInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}