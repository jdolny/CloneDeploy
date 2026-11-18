using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using CloneDeploy_Agent.Contracts;
namespace CloneDeploy_Agent.MacOS;

public sealed record MacCycleResult(string Status, TimeSpan RetryAfter, Guid? DeviceGuid = null, AgentUpdateOffer? Update = null);

/// <summary>Enrollment and check-in. Mac updates use their own offer; Windows updates are never consumed.</summary>
public sealed class MacEnrollmentClient : IDisposable
{
    private const int MinimumRetrySeconds = 10;
    private const int MaximumRetrySeconds = 3600;
    private const int MaximumCheckinSeconds = 86400;
    private const int MaximumCheckinResponseBytes = 256 * 1024;

    private readonly MacConfiguration configuration;
    private readonly MacIdentity identity;
    private readonly AgentHttpClientPool http;
    private readonly string? hardwareUuid;
    public MacEnrollmentClient(MacConfiguration configuration, MacIdentity identity, string directory, string? hardwareUuid = null)
    {
        this.configuration = configuration; this.identity = identity; this.hardwareUuid = hardwareUuid;
        http = new(configuration.Options(directory));
    }
    public UserLoginJournal? UserLogins { get; init; }
    public MacLoginTracker? LoginTracker { get; init; }
    public AgentBranding Branding { get; private set; } = new();
    public AgentFeatures Features { get; private set; } = new();
    private sealed record Enrollment(int SchemaVersion, Guid DeviceGuid, string CertificatePem, string CaCertificatePem);
    private sealed record Pending(int SchemaVersion, Guid RequestId, string Status, int RetryAfterSeconds);
    private sealed record Checkin(int SchemaVersion, Guid DeviceGuid, int CheckinIntervalSeconds, string? Platform = null, AgentUpdateOffer? MacUpdate = null, string? BrandingVersion = null, AgentFeatures? Features = null, bool UserLoginsSubmitted = false, AgentCertificateRenewal? CertificateRenewal = null);
    public async Task<MacCycleResult> RunOnceAsync(CancellationToken ct)
    {
        if (identity.State.CertificatePem is null)
        {
            using var client = http.Create();
            var macs = NetworkInterface.GetAllNetworkInterfaces().Where(x => x.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
                .Select(x => x.GetPhysicalAddress().ToString()).Where(x => x.Length == 12).Distinct().Take(64).ToArray();
            using var response = await client.PostAsJsonAsync("api/agent/v1/enroll", new
            {
                configuration.ProvisioningKey, identity.State.DeviceName, identity.State.InstallationId, identity.State.CsrPem,
                identity.State.RequestReset, HardwareUuid = hardwareUuid, MacAddresses = macs
            }, ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests) return RateLimited(response);
            if (response.StatusCode == HttpStatusCode.Conflict && !identity.State.RequestReset)
            {
                identity.ResetEnrollment();
                http.Invalidate();
                return new("RecoveryRequested", TimeSpan.FromSeconds(1));
            }
            if (response.StatusCode == HttpStatusCode.Accepted)
            {
                var pending = await response.Content.ReadFromJsonAsync<Pending>(ct) ?? throw new InvalidDataException("Empty enrollment response.");
                if (pending.SchemaVersion != 1 || pending.RequestId == Guid.Empty || pending.Status is not ("PendingApproval" or "PendingPreProvision" or "PendingReset"))
                    throw new InvalidDataException("Unsupported pending enrollment response.");
                return new(pending.Status, TimeSpan.FromSeconds(Math.Clamp(pending.RetryAfterSeconds, MinimumRetrySeconds, MaximumRetrySeconds)));
            }
            RequireSuccess(response, "Enrollment");
            var enrollment = await response.Content.ReadFromJsonAsync<Enrollment>(ct) ?? throw new InvalidDataException("Empty enrollment response.");
            if (enrollment.SchemaVersion != 1) throw new InvalidDataException("Unsupported enrollment response version.");
            identity.AcceptCertificate(enrollment.DeviceGuid, enrollment.CertificatePem, enrollment.CaCertificatePem);
        }
        AgentCertificateLifecycle.ThrowIfExpired(identity.State.CertificatePem);
        using var certificate = identity.GetClientCertificate() ?? throw new InvalidDataException("Missing agent certificate.");
        using var authenticated = http.Create(certificate);
        authenticated.MaxResponseContentBufferSize = MaximumCheckinResponseBytes;
        if (LoginTracker is not null) await LoginTracker.SampleAsync(ct);
        var logins = UserLogins?.Pending();
        using var checkin = await authenticated.PostAsJsonAsync("api/agent/v1/check-in", new
        {
            ComputerName = Environment.MachineName,
            ClientVersion = typeof(MacEnrollmentClient).Assembly.GetName().Version!.ToString(), Architecture = "arm64", Platform = AgentPlatform.MacOS, UserLogins = logins, CertificateRenewalCsr = identity.PrepareRenewal(), SupportsCertificateRenewal = true, SupportsMacPackageUpdates = true
        }, ct);
        if (checkin.StatusCode == HttpStatusCode.TooManyRequests) return RateLimited(checkin);
        RequireSuccess(checkin, "Check-in");
        var result = await checkin.Content.ReadFromJsonAsync<Checkin>(ct) ?? throw new InvalidDataException("Empty check-in response.");
        if (result.SchemaVersion != 1 || result.DeviceGuid != identity.State.DeviceGuid)
            throw new InvalidDataException("Check-in response does not match this device.");
        if (result.Platform != AgentPlatform.MacOS)
            throw new InvalidDataException("Update the server to a version that supports macOS enrollment before using this agent.");
        identity.CompleteRenewal();
        if (result.CertificateRenewal is { } renewal)
        {
            identity.AcceptRenewal(renewal);
            http.Invalidate();
        }
        if (result.UserLoginsSubmitted && logins is not null) UserLogins!.Acknowledge(logins);
        Features = result.Features ?? new();
        if (result.BrandingVersion is not null && Branding.Version != result.BrandingVersion)
        {
            try
            {
                var branding = await authenticated.GetFromJsonAsync<AgentBranding>("api/agent/v1/branding", ct);
                if (branding is not null && branding.LogoPng is not { Length: > CloneDeploy_Agent.Contracts.AgentBranding.MaximumLogoBytes }) Branding = branding;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or System.Text.Json.JsonException or OperationCanceledException)
            { Console.Error.WriteLine("Portal branding unavailable; retrying at next check-in."); }
        }
        return new("CheckedIn", TimeSpan.FromSeconds(Math.Clamp(result.CheckinIntervalSeconds, MinimumRetrySeconds, MaximumCheckinSeconds)), result.DeviceGuid, result.MacUpdate);
    }
    private static MacCycleResult RateLimited(HttpResponseMessage response)
    {
        var delay = response.Headers.RetryAfter?.Delta ?? response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow ?? TimeSpan.FromMinutes(1);
        return new("RateLimited", TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, MinimumRetrySeconds, MaximumRetrySeconds)));
    }
    private static void RequireSuccess(HttpResponseMessage response, string operation)
    {
        if (response.StatusCode == HttpStatusCode.OK) return;
        var guidance = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Check the provisioning key or enrolled device certificate. Identity was retained.",
            HttpStatusCode.Forbidden => "Review the device and approval request on the server. Identity was retained.",
            HttpStatusCode.Conflict => "Review the existing device and reset request on the server. Identity was retained.",
            _ => "Check server availability and configuration."
        };
        // Never echo response bodies or enrollment secrets into the daemon log.
        throw new HttpRequestException($"{operation} returned HTTP {(int)response.StatusCode}. {guidance}", null, response.StatusCode);
    }
    public void Dispose() => http.Dispose();
}
