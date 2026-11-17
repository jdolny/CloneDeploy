using System.Net;
using System.Net.Http.Json;
using CloneDeploy.SiteServices;

namespace CloneDeploy.SiteService;

// Outbound enrollment and polling only. Never constructs a web host or listener.
public sealed class ManagedWindowsWorker(SiteLocalStore store, WindowsWorkWorker worker, ILogger<ManagedWindowsWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var state = store.Load();
        if (!state.Package.IsWindowsWorker) throw new InvalidDataException("Use a Windows Worker enrollment package.");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var pending = state.Configuration is null;
                try
                {
                    using var http = SiteEnrollmentClient.CreateClient(state, !pending);
                    using var response = pending
                        ? await http.PostAsJsonAsync("/api/site-services/enroll", SiteEnrollmentClient.Request(state), ct)
                        : await http.PostAsJsonAsync("/api/site-services/status", new SiteStatusRequest(state.Configuration!.Revision, false, worker.Error, SiteEnrollmentClient.Version) { HostOs = "Windows" }, ct);
                    if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) worker.Configure(null);
                    response.EnsureSuccessStatusCode();
                    var configuration = pending ? (await response.Content.ReadFromJsonAsync<SiteEnrollmentReply>(ct))?.Configuration
                        : await response.Content.ReadFromJsonAsync<SiteConfiguration>(ct);
                    if (configuration is not null)
                    {
                        if (!configuration.IsWindowsWorker || configuration.ProxyEnabled || configuration.Boot.PxeEnabled || configuration.Boot.TftpEnabled)
                            throw new InvalidDataException("Main returned an incompatible worker configuration.");
                        state = SiteEnrollmentClient.Accept(state, configuration, store);
                        worker.Configure(state);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogWarning("Worker enrollment/status failed: {Error}", SiteFailureDiagnostics.Describe(ex)); }
                await Task.Delay(TimeSpan.FromSeconds(pending ? 10 : 60), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { worker.Configure(null); }
    }
}
