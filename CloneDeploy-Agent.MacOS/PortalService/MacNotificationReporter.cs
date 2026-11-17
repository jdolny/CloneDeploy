using System.Net.Http.Json;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent.MacOS;

internal sealed class MacNotificationReporter(MacConfiguration configuration, MacIdentity identity, string directory, PortalNotificationJournal journal)
{
    private const int MaximumResponseBytes = 1024 * 1024;

    internal async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var certificate = identity.GetClientCertificate();
                if (certificate is not null)
                {
                    var reports = journal.PendingReports();
                    if (reports.Length > 0)
                    {
                        using var pool = new AgentHttpClientPool(configuration.Options(directory));
                        using var client = pool.Create(certificate);
                        client.Timeout = TimeSpan.FromSeconds(20);
                        client.MaxResponseContentBufferSize = MaximumResponseBytes;
                        using var response = await client.PostAsJsonAsync("api/agent/v1/portal/notifications/receipts", reports, ct);
                        response.EnsureSuccessStatusCode();
                        var acknowledgments = await response.Content.ReadFromJsonAsync<AgentNotificationAcknowledgment[]>(ct) ?? [];
                        foreach (var report in reports) journal.MarkReported(report);
                        journal.ApplyAcknowledgments(acknowledgments);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { Console.Error.WriteLine($"Notification receipts remain queued ({ex.GetType().Name})."); }
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }
}
