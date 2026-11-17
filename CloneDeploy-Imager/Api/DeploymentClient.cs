using CloneDeploy_Agent.Contracts;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using CloneDeploy_Imager.FfuFileSystem.Reading;
using CloneDeploy_Imager.Imaging;

namespace CloneDeploy_Imager.Api;

/// <summary>
/// Carries the server-approved image metadata, source transport, and settings for a claimed deployment.
/// </summary>
internal sealed record DeploymentExecution(Guid DeploymentId, string Format, long SizeBytes, string Sha256, bool Pipable)
{
    public MulticastConnection? Multicast { get; init; }
    public SmbCapture? Direct { get; init; }
    public string FfuTransport { get; init; } = "Smb";
    public string WimTransport { get; init; } = "Https";
    public DeploymentPerformance Performance { get; init; } = new();
    public List<DeploymentDriverArchive> Drivers { get; init; } = [];
    public string? DriverWarning { get; init; }
    public string ComputerName { get; init; } = "";
    public bool ConfigureDomainJoinOu { get; init; }
    public bool SetComputerName { get; init; }
    public DeploymentCompletionAction AfterDeployment { get; init; }
}
internal sealed record DeploymentDriverArchive(Guid Id, string Sha256, string Name);
/// <summary>
/// Specifies deployment verification and HTTPS FFU cache and concurrency settings.
/// </summary>
internal sealed record DeploymentPerformance(bool VerifyImage = true, int BlockMiB = 8, int CacheMiB = 64, int ParallelReads = 4);

/// <summary>
/// Handles deployment admission, image downloads, progress, lease renewal, and completion reporting.
/// </summary>
internal sealed partial class ImagingClient
{
    private const int DeploymentRenewalSeconds = 900;
    private const int DeploymentDownloadBufferBytes = 65536;

    internal Func<SmbCapture, (string Path, IDisposable Connection)> ConnectWimSmb { get; init; } = WimSmbConnection.Connect;
    public async Task DownloadDeploymentDriverAsync(DeploymentDriverArchive driver, string destination, CancellationToken ct)
    {
        using var response = await DeploymentRequest(HttpMethod.Get, $"drivers/{driver.Id:D}", null, ct, streaming: true);
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, DeploymentDownloadBufferBytes, true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[DeploymentDownloadBufferBytes];
        while (true)
        {
            using var inactivity = Deadline(ct);
            var count = await source.ReadAsync(buffer, inactivity.Token);
            if (count == 0) break;
            hash.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(driver.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Driver package checksum mismatch: {driver.Name}");
    }
    /// <summary>
    /// Reports imaging-slot admission, queue position, active deployments, and the configured limit.
    /// </summary>
    internal sealed record DeploymentQueue(bool Admitted, int Position, int Active, int Limit);
    public async Task<DeploymentQueue> EnterDeploymentQueueAsync(CancellationToken ct)
    {
        using var response = await DeploymentRequest(HttpMethod.Post, "queue", null, ct);
        var result = await response.Content.ReadFromJsonAsync<DeploymentQueue>(ct);
        if (result is null || result.Limit < 0 || result.Active < 0 || result.Position < 0 ||
            (result.Admitted ? result.Position != 0 : result.Position == 0))
            throw new InvalidDataException("Invalid deployment queue response.");
        return result;
    }
    public async Task<DeploymentExecution> GetDeploymentExecutionAsync(Guid expected, CancellationToken ct)
    {
        using var response = await DeploymentRequest(HttpMethod.Get, "execution", null, ct);
        var plan = await response.Content.ReadFromJsonAsync<DeploymentExecution>(ct);
        if (plan is null || plan.DeploymentId != expected || plan.Format is not ("Wim" or "Ffu") || plan.SizeBytes <= 0 ||
            plan.Sha256 is not { Length: 64 } || !plan.Sha256.All(Uri.IsHexDigit) || plan.Format == "Wim" && !plan.Pipable)
            throw new InvalidDataException("Invalid deployment image metadata.");
        if (!Enum.IsDefined(plan.AfterDeployment)) throw new InvalidDataException("Invalid deployment completion action.");
        if (plan.Performance is not { } tuning || !new FfuReadOptions(tuning.BlockMiB, tuning.CacheMiB, tuning.ParallelReads).Valid)
            throw new InvalidDataException("Invalid deployment performance settings.");
        if (plan.Format == "Ffu" && plan.FfuTransport is not ("Smb" or "Https"))
            throw new InvalidDataException("Unsupported FFU transport.");
        if (plan.Format == "Wim" && plan.WimTransport is not ("Smb" or "Https" or "Multicast"))
            throw new InvalidDataException("Unsupported WIM transport.");
        if (plan.WimTransport == "Multicast" && (plan.Format != "Wim" || plan.Multicast is not { } multicast ||
            multicast.SessionId == Guid.Empty || multicast.Port is < MinimumDeploymentMulticastPort or > MaximumDeploymentMulticastPort || multicast.Port % 2 != 0 ||
            !IPAddress.TryParse(multicast.SenderAddress, out var sender) || sender.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            sender.GetAddressBytes()[0] is 0 or >= MulticastFirstOctet || !IPAddress.TryParse(multicast.GroupAddress, out var group) ||
            group.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || group.GetAddressBytes()[0] != AdministrativelyScopedMulticastFirstOctet))
            throw new InvalidDataException("Invalid multicast deployment connection.");
        if (plan.Format == "Ffu" && plan.FfuTransport == "Smb" || plan.Format == "Wim" && plan.WimTransport == "Smb")
        {
            using var smbResponse = await DeploymentRequest(HttpMethod.Get, "smb", null, ct);
            var direct = await smbResponse.Content.ReadFromJsonAsync<SmbCapture>(ct)
                ?? throw new InvalidDataException("Missing image SMB connection.");
            plan = plan with { Direct = direct };
            if (plan.Format == "Ffu") CloneDeploy_Imager.Deployment.FfuDeploymentSource.Validate(plan);
            else WimSmbConnection.ValidateDeployment(plan);
        }
        return plan;
    }
    public async Task RenewDeploymentAsync(Guid expected, CancellationToken ct)
    {
        using var response = await DeploymentRequest(HttpMethod.Post, "renew", null, ct);
        var session = await response.Content.ReadFromJsonAsync<DeploymentSession>(ct);
        if (session?.Assignment?.DeploymentId != expected || session.TokenType != "Bearer" ||
            string.IsNullOrEmpty(session.AccessToken) || !session.AccessToken.StartsWith("cdi_deploy_", StringComparison.Ordinal) ||
            session.AccessToken.Length > MaximumAccessTokenCharacters || session.AccessToken.Any(char.IsWhiteSpace) || session.ExpiresIn != DeploymentRenewalSeconds)
            throw new InvalidDataException("Invalid deployment renewal.");
        deploymentToken = session.AccessToken;
    }
    public async Task ReportDeploymentProgressAsync(CloneDeploy_Imager.Imaging.DeploymentProgress progress, CancellationToken ct)
    {
        using var content = JsonContent.Create(new { phase = (int)progress.Stage, percent = progress.Percent, networkBytesPerSecond = progress.NetworkBytesPerSecond, statusMessage = progress.Status });
        using var response = await DeploymentRequest(HttpMethod.Post, "progress", content, ct);
    }

    public Task FinishDeploymentAsync(bool success, CancellationToken ct) => FinishDeploymentAsync(success, null, ct);

    public async Task FinishDeploymentAsync(bool success, bool? agentInstalled, CancellationToken ct)
    {
        using var content = JsonContent.Create(new { success, agentInstalled, diagnostics = log.Snapshot() });
        using var response = await DeploymentRequest(HttpMethod.Post, "finish", content, ct);
    }
    public async Task CopyDeploymentImageAsync(DeploymentExecution image, Stream destination, CancellationToken ct)
    {
        if (image.Format != "Wim") throw new InvalidOperationException("Streaming deployment requires a WIM image.");
        if (image.WimTransport == "Multicast") { await ReceiveMulticastAsync(image, destination, ct); return; }
        if (image.WimTransport is not ("Smb" or "Https")) throw new InvalidDataException("Unsupported WIM transport.");
        IDisposable? connection = null;
        try
        {
            string? smbPath = null;
            if (image.WimTransport == "Smb")
            {
                WimSmbConnection.ValidateDeployment(image);
                var source = ConnectWimSmb(image.Direct!);
                connection = source.Connection;
                smbPath = source.Path;
            }
            using var response = smbPath is null ? await DeploymentRequest(HttpMethod.Get, "image", null, ct, streaming: true) : null;
            if (response?.Content.Headers.ContentLength is { } length && length != image.SizeBytes)
                throw new InvalidDataException("Image download size differs from the assigned version.");
            await using var input = smbPath is null ? await response!.Content.ReadAsStreamAsync(ct)
                : new FileStream(smbPath, FileMode.Open, FileAccess.Read, FileShare.Read, FileTransferBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (input.CanSeek && input.Length != image.SizeBytes) throw new InvalidDataException("SMB WIM size differs from the assigned version.");
            log.Write("WIM deployment transport: " + image.WimTransport);
            using var hash = image.Performance.VerifyImage ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
            var buffer = new byte[FileTransferBufferBytes];
            long received = 0;
            while (true)
            {
                using var inactivity = Deadline(ct);
                var count = await input.ReadAsync(buffer, inactivity.Token);
                if (count == 0) break;
                received += count;
                if (received > image.SizeBytes) throw new InvalidDataException("Image download exceeds the assigned version size.");
                hash?.AppendData(buffer, 0, count);
                await destination.WriteAsync(buffer.AsMemory(0, count), ct);
            }
            if (received != image.SizeBytes || hash is not null && !Convert.ToHexString(hash.GetHashAndReset()).Equals(image.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Image download checksum verification failed. Deployment cannot complete.");
            await destination.FlushAsync(ct);
        }
        finally { connection?.Dispose(); }
    }
    private async Task<HttpResponseMessage> DeploymentRequest(HttpMethod method, string path, HttpContent? content, CancellationToken ct, bool streaming = false)
    {
        var credential = deploymentToken ?? throw new SessionExpiredException();
        using var deadline = Deadline(ct);
        using var request = new HttpRequestMessage(method, "api/imaging/deployment/" + path) { Content = content };
        request.Headers.Authorization = new("Bearer", credential);
        request.Headers.Add("X-Imager-OU", "1");
        if (streaming) ImagerResponseVerification.MarkDownload(request);
        var response = await http.SendAsync(request, streaming ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, deadline.Token);
        if (response.IsSuccessStatusCode) return response;
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized) throw new SessionExpiredException();
            string? detail = null;
            try
            {
                var problem = await response.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
                if (problem.TryGetProperty("detail", out var property) && property.ValueKind == JsonValueKind.String) detail = property.GetString();
            }
            catch (JsonException) { }
            throw new InvalidOperationException(detail ?? $"Deployment request failed (HTTP {(int)response.StatusCode}).");
        }
    }
}
