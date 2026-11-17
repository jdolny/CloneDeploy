using System.Net;
using System.Net.Http.Json;
using CloneDeploy.SiteServices;
using System.Text.Json;
namespace CloneDeploy.SiteService;

public sealed class WindowsWorkWorker(SiteLocalStore store, WindowsWorkEngine engine, ILogger<WindowsWorkWorker> logger) : BackgroundService
{
    private static readonly TimeSpan HttpRequestTimeout = TimeSpan.FromHours(6);
    private static readonly TimeSpan JobTimeout = TimeSpan.FromHours(5);
    private const int MaximumResponseBytes = 1024 * 1024;
    private const int MaximumStatusErrorCharacters = 900;
    private const int MaximumCompletionErrorCharacters = 2000;
    private const int MaximumInputFiles = 256;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProgressTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(10);

    private SiteLocalState? desired;
    private CancellationTokenSource? active;
    private readonly object configurationLock = new();
    public string? Error { get; private set; }
    public void Configure(SiteLocalState? state)
    {
        lock (configurationLock)
        {
            var previous = Interlocked.Exchange(ref desired, state);
            if (previous?.Configuration?.WindowsWorkerRoles != state?.Configuration?.WindowsWorkerRoles) active?.Cancel();
        }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var state = Volatile.Read(ref desired);
                if (state?.Configuration?.WindowsWorkerRoles is { } roles && roles != WindowsWorkerRoles.None)
                {
                    if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Windows worker roles require Windows.");
                    using var http = SiteEnrollmentClient.CreateClient(state, true); http.Timeout = HttpRequestTimeout;
                    http.MaxResponseContentBufferSize = MaximumResponseBytes;
                    await Health(http, roles, stoppingToken);
                    using var response = await http.PostAsync("/api/site-services/windows/claim", null, stoppingToken); response.EnsureSuccessStatusCode();
                    if (response.StatusCode != HttpStatusCode.NoContent)
                    {
                        var job = (await response.Content.ReadFromJsonAsync<WindowsWorkOffer>(stoppingToken))!;
                        using var stop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        lock (configurationLock)
                        {
                            if (desired?.Configuration?.RegistrationId != state.Configuration.RegistrationId ||
                                (desired.Configuration.WindowsWorkerRoles & WindowsWorkFiles.Role(job.Kind)) == 0) stop.Cancel();
                            active = stop;
                        }
                        stop.CancelAfter(JobTimeout);
                        try { stop.Token.ThrowIfCancellationRequested(); await Run(http, roles, job, stop); } finally { lock (configurationLock) active = null; }
                    }
                    Error = null;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { Error = "Windows worker: " + ex.Message[..Math.Min(MaximumStatusErrorCharacters, ex.Message.Length)]; logger.LogWarning("{Error}", Error); }
            try { await Task.Delay(PollInterval, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
    private async Task Run(HttpClient http, WindowsWorkerRoles roles, WindowsWorkOffer job, CancellationTokenSource stop)
    {
        var root = Path.Combine(store.DirectoryPath, "WindowsJobs"); Directory.CreateDirectory(root);
        var folder = Path.Combine(root, job.Id.ToString("N") + "-" + job.Lease.ToString("N"));
        Directory.CreateDirectory(folder); CloneDeploy_Server.WinPe.BuildStorage.RejectLinks(folder);
        using var monitorStop = new CancellationTokenSource(); var monitor = Monitor();
        var accepted = false;
        try
        {
            if ((roles & WindowsWorkFiles.Role(job.Kind)) == 0 || job.Inputs.Length > MaximumInputFiles) throw new InvalidDataException("Work capability mismatch.");
            foreach (var file in job.Inputs)
            {
                WindowsWorkFiles.Validate(file);
                using var download = await http.GetAsync(Url("input/" + Uri.EscapeDataString(file.Name)), HttpCompletionOption.ResponseHeadersRead, stop.Token);
                download.EnsureSuccessStatusCode(); await using var stream = await download.Content.ReadAsStreamAsync(stop.Token);
                await WindowsWorkFiles.CopyAsync(stream, Path.Combine(folder, file.Name), file, stop.Token);
            }
            var result = await engine.RunAsync(job, folder, stop.Token);
            var outputs = await Upload(result.Outputs);
            using var completed = await http.PostAsJsonAsync(Url("complete"), new WindowsWorkCompletion(null, result.Result, outputs.ToArray()), stop.Token);
            completed.EnsureSuccessStatusCode(); accepted = true;
        }
        catch (Exception ex) when (!stop.IsCancellationRequested)
        {
            var logs = await Upload(engine.Logs(job));
            using var response = await http.PostAsJsonAsync(Url("complete"), new WindowsWorkCompletion(ex.Message[..Math.Min(MaximumCompletionErrorCharacters, ex.Message.Length)], null, logs.ToArray()), stop.Token);
            response.EnsureSuccessStatusCode(); accepted = true;
        }
        finally
        {
            monitorStop.Cancel(); await monitor;
            CloneDeploy_Server.WinPe.BuildStorage.RejectLinks(folder); Directory.Delete(folder, true);
            if (accepted && job.Kind == "WinPe")
            {
                var buildFolder = engine.Builds.Job(job.Id);
                CloneDeploy_Server.WinPe.BuildStorage.RejectLinks(buildFolder);
                if (engine.Builder.CanDeleteQueueJob(job.Id) && Directory.Exists(buildFolder)) Directory.Delete(buildFolder, true);
            }
        }
        string Url(string suffix) => $"/api/site-services/windows/{job.Id}/{job.Lease}/{suffix}";
        async Task<List<WindowsWorkFile>> Upload(Dictionary<string, string> paths)
        {
            var outputs = new List<WindowsWorkFile>();
            foreach (var (name, path) in paths)
            {
                var file = await WindowsWorkFiles.DescribeAsync(path, name, stop.Token);
                await using var input = File.OpenRead(path); using var content = new StreamContent(input);
                using var uploaded = await http.PutAsync(Url($"output/{Uri.EscapeDataString(name)}?length={file.Length}&sha256={file.Sha256}"), content, stop.Token);
                uploaded.EnsureSuccessStatusCode(); outputs.Add(file);
            }
            return outputs;
        }
        async Task Monitor()
        {
            try
            {
                while (!monitorStop.IsCancellationRequested)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(monitorStop.Token); deadline.CancelAfter(ProgressTimeout);
                    using var response = await http.PostAsJsonAsync(Url("renew"), new WindowsWorkProgress(engine.Progress(job)), deadline.Token);
                    response.EnsureSuccessStatusCode(); await Health(http, roles, deadline.Token);
                    await Task.Delay(ProgressInterval, monitorStop.Token);
                }
            }
            catch (OperationCanceledException) when (monitorStop.IsCancellationRequested) { }
            catch { stop.Cancel(); }
        }
    }
    private async Task Health(HttpClient http, WindowsWorkerRoles roles, CancellationToken ct)
    {
        if ((roles & WindowsWorkerRoles.WinPe) == 0) return;
        using var response = await http.PostAsJsonAsync("/api/site-services/windows/health",
            new WindowsWorkerHealth(JsonSerializer.Serialize(engine.Health()), JsonSerializer.Serialize(engine.Builder.SetupHealth())), ct);
        response.EnsureSuccessStatusCode();
    }
}
