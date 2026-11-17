using System.Security.Cryptography;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CloneDeploy_Imager.Configuration;
using CloneDeploy_Imager.Diagnostics;
using CloneDeploy_Imager.Discovery;
using CloneDeploy_Imager.Imaging;

namespace CloneDeploy_Imager.Api;

/// <summary>
/// Owns the HTTP connection, credentials, request deadlines, and shared imaging request handling.
/// </summary>
internal sealed partial class ImagingClient : IDisposable
{
    private const int MaximumJsonResponseBytes = 64 * 1024;
    private const int ClaimKeyBytes = 32;
    private const int MaximumAccessTokenCharacters = 4096;
    private const int MinimumCaptureChunkBytes = 65536;
    private const int MaximumCaptureChunkBytes = 8 * 1024 * 1024;
    private const int FileTransferBufferBytes = 1024 * 1024;
    private const int MinimumDeploymentMulticastPort = 39000;
    private const int MaximumDeploymentMulticastPort = 40998;
    private const int MulticastFirstOctet = 224;
    private const int AdministrativelyScopedMulticastFirstOctet = 239;

    private readonly HttpClient http;
    private readonly DiagnosticLog log;
    private readonly TimeSpan requestTimeout;
    private readonly string claimKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(ClaimKeyBytes));
    private string? deploymentToken;
    private string? token;
    private readonly object sessionGate = new();
    private DateTimeOffset expiresAt;
    public LinkedImagingDevice? Device { get; private set; }
    private LinkedImagingDevice? lastLogDevice;
    private (string DeviceLinkToken, string Contents)? pendingFailureLog;
    public bool IsSignedIn => token is not null && DateTimeOffset.UtcNow < expiresAt;

    public ImagingClient(Uri server, int timeoutSeconds, DiagnosticLog log, HttpMessageHandler? handler = null)
    {
        this.log = log;
        requestTimeout = TimeSpan.FromSeconds(timeoutSeconds);
        if (handler is null)
        {
            var transport = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
            handler = new ImagerSecureSession(transport, new ImagerSettings().ServerCaCertificatePath);
        }
        http = new HttpClient(new CloneDeploy_Agent.Contracts.SiteDownloadClient(handler))
        {
            BaseAddress = ImagerSettings.ServerAddress(server.AbsoluteUri),
            Timeout = Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = MaximumJsonResponseBytes
        };
    }

    public ImagingClient(Uri server, int timeoutSeconds, DiagnosticLog log, string serverCaCertificatePath)
        : this(server, timeoutSeconds, log, new ImagerSecureSession(
            new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }, serverCaCertificatePath)) { }

    public async Task<bool> TryUploadFailureLogAsync()
    {
        var device = Device ?? lastLogDevice;
        if (device is null) return false;
        pendingFailureLog = (device.LinkToken, log.Snapshot());
        return await RetryPendingFailureLogAsync();
    }

    public async Task<bool> RetryPendingFailureLogAsync()
    {
        if (pendingFailureLog is not { } pending || !IsSignedIn) return false;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var content = JsonContent.Create(new { deviceLinkToken = pending.DeviceLinkToken, contents = pending.Contents });
            using var response = await UploadRequest(HttpMethod.Post, "device/logs", content, deadline.Token);
            pendingFailureLog = null;
            return true;
        }
        catch (Exception ex) { log.Failure("Failure log upload could not complete", ex); return false; }
    }

    private CancellationTokenSource Deadline(CancellationToken ct, TimeSpan? timeout = null)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout ?? requestTimeout);
        return deadline;
    }

    private async Task<HttpResponseMessage> UploadRequest(HttpMethod method, string path, HttpContent? content,
        CancellationToken ct, TimeSpan? timeout = null)
    {
        using var deadline = Deadline(ct, timeout);
        using var request = CaptureRequest(method, path, content);
        var response = await http.SendAsync(request, deadline.Token);
        if (response.IsSuccessStatusCode) return response;
        using (response)
        {
            if (path.Contains("/ffu?", StringComparison.Ordinal))
                log.Write($"HTTPS FFU request rejected: HTTP {(int)response.StatusCode}; {path}.");
            if (response.StatusCode == HttpStatusCode.Unauthorized) { SignOut(); throw new SessionExpiredException(); }
            string? message = null;
            try
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
                if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("message", out var detail) && detail.ValueKind == JsonValueKind.String) message = detail.GetString();
            }
            catch (JsonException) { }
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                log.Write($"Imaging request rejected: HTTP 403; {method} {path.Split('?')[0]}.");
                throw new InvalidOperationException(message ?? "Access denied. Your account may not have the required imaging permission, or access to this resource is restricted.");
            }
            throw new InvalidOperationException(message ?? $"Imaging request failed (HTTP {(int)response.StatusCode}).");
        }
    }

    private static async Task<string?> ErrorCode(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.String ? error.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private HashSet<string> permissions = new(StringComparer.Ordinal);
    public bool Can(string permission) => permissions.Contains(permission);
    public void SignOut()
    {
        lock (sessionGate)
        {
            if (Device is not null) lastLogDevice = Device;
            permissions.Clear(); preparedCapture = null; preparedUpload = null; deploymentToken = null;
            token = null; expiresAt = default; Device = null;
        }
    }
    public void Dispose() { SignOut(); http.Dispose(); }
}

/// <summary>
/// Signals that authentication has expired or access was revoked and sign-in is required.
/// </summary>
internal sealed class SessionExpiredException : Exception;
