using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CloneDeploy_Agent;

/// <summary>
/// Describes whether a check-in succeeded, is waiting for approval, or needs another attempt.
/// </summary>
public enum AgentCycleStatus
{
    CheckedIn,
    PendingApproval,
    PendingPreProvision,
    PendingReset,
    RecoveryRequested,
    RateLimited,
    WaitingForWindowsSetup
}

/// <summary>
/// Carries the check-in outcome and how long the agent should wait before trying again.
/// </summary>
public sealed record AgentCycleResult(AgentCycleStatus Status, TimeSpan RetryAfter, Guid? DeviceGuid = null)
{
    public string? EnrollmentWaitReason { get; init; }
    public CloneDeploy_Agent.Contracts.AgentUpdateOffer? Update { get; init; }

    public bool CanRunPolicies(string installedVersion)
    {
        if (Status != AgentCycleStatus.CheckedIn)
            return false;

        if (Update is null)
            return true; // No release has been prepared on this server.
        Update.Validate();

        return CloneDeploy_Agent.Contracts.AgentUpdateOffer.ParseVersion(Update.Version) ==
               CloneDeploy_Agent.Contracts.AgentUpdateOffer.ParseVersion(installedVersion);
    }
}

/// <summary>
/// Enrolls the device, checks in with the server, and submits pending user login history.
/// </summary>
public sealed class AgentClient(AgentOptions options, AgentIdentity identity) : IDisposable
{
    private const int MaximumMetadataResponseBytes = 64 * 1024;
    private const int MaximumContentResponseBytes = 1024 * 1024;
    private const int MinimumCheckInSeconds = 10;
    private const int MaximumRetrySeconds = 60 * 60;
    private const int MaximumCheckInSeconds = 24 * 60 * 60;

    private readonly AgentHttpClientPool http = new(options);

    public async Task<CloneDeploy_Agent.Contracts.HelpResponse> HelpAsync(string sid, CloneDeploy_Agent.Contracts.HelpRequest request, CancellationToken ct)
    {
        using var certificate = identity.GetClientCertificate() ?? throw new IOException("The agent is not enrolled yet.");
        using var client = CreateClient(certificate);
        client.MaxResponseContentBufferSize = CloneDeploy_Agent.Contracts.HelpAttachmentLimits.PayloadBytes;
        using var response = await client.PostAsJsonAsync("api/agent/v1/help", new CloneDeploy_Agent.Contracts.HelpAgentRequest(sid, request), ct);
        if (response.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Forbidden)
            return await response.Content.ReadFromJsonAsync<CloneDeploy_Agent.Contracts.HelpResponse>(ct) ?? new("Help is unavailable.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CloneDeploy_Agent.Contracts.HelpResponse>(ct) ?? new("Help is unavailable.");
    }

    public async Task<CloneDeploy_Agent.Contracts.AgentPortalIdentity> GetPortalIdentityAsync(
        string sid,
        CancellationToken ct)
    {
        using var certificate = identity.GetClientCertificate()
                                ?? throw new IOException("The agent is not enrolled yet.");
        using var client = CreateClient(certificate);
        client.MaxResponseContentBufferSize = MaximumMetadataResponseBytes;

        return await client.GetFromJsonAsync<CloneDeploy_Agent.Contracts.AgentPortalIdentity>(
                   $"api/agent/v1/portal/identity?sid={Uri.EscapeDataString(sid)}", ct)
               ?? throw new IOException("The server returned no portal identity.");
    }

    public async Task<CloneDeploy_Agent.Contracts.AgentPortalPolicy[]> GetPortalPoliciesAsync(CancellationToken ct)
    {
        using var certificate = identity.GetClientCertificate()
                                ?? throw new IOException("The agent is not enrolled yet.");
        using var client = CreateClient(certificate);
        client.MaxResponseContentBufferSize = MaximumContentResponseBytes;

        return await client.GetFromJsonAsync<CloneDeploy_Agent.Contracts.AgentPortalPolicy[]>(
                   "api/agent/v1/portal/policies",
                   ct)
               ?? [];
    }

    public async Task<CloneDeploy_Agent.Contracts.AgentPortalPolicy[]> GetAvailablePoliciesAsync(
        string sid,
        CancellationToken ct)
    {
        using var certificate = identity.GetClientCertificate()
                                ?? throw new IOException("The agent is not enrolled yet.");
        using var client = CreateClient(certificate);
        client.MaxResponseContentBufferSize = MaximumContentResponseBytes;

        return await client.GetFromJsonAsync<CloneDeploy_Agent.Contracts.AgentPortalPolicy[]>(
                   $"api/agent/v1/portal/available?sid={Uri.EscapeDataString(sid)}", ct)
               ?? [];
    }

    public async Task<byte[]> GetPortalIconAsync(int policyId, string sid, CancellationToken ct)
    {
        using var certificate = identity.GetClientCertificate()
                                ?? throw new IOException("The agent is not enrolled yet.");
        using var client = CreateClient(certificate);
        client.MaxResponseContentBufferSize = MaximumMetadataResponseBytes;

        return await client.GetByteArrayAsync(
            $"api/agent/v1/portal/icons/{policyId}?sid={Uri.EscapeDataString(sid)}",
            ct);
    }

    public async Task<CloneDeploy_Agent.Contracts.AgentNotificationAcknowledgment[]> ReportNotificationsAsync(
        CloneDeploy_Agent.Contracts.AgentNotificationReceipt[] reports,
        CancellationToken ct)
    {
        using var certificate = identity?.GetClientCertificate()
                                ?? throw new IOException("The agent is not enrolled yet.");
        using var client = CreateClient(certificate);
        using var response = await client.PostAsJsonAsync("api/agent/v1/portal/notifications/receipts", reports, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CloneDeploy_Agent.Contracts.AgentNotificationAcknowledgment[]>(ct) ?? [];
    }

    public UserLoginJournal? UserLogins { get; set; }

    public UserLoginTracker? LoginTracker { get; set; }

    /// <summary>
    /// Carries the device identity and certificates returned after enrollment.
    /// </summary>
    private sealed record EnrollmentResponse(
        int SchemaVersion,
        Guid DeviceGuid,
        string CertificatePem,
        string CaCertificatePem,
        DateTime ExpiresAtUtc);

    /// <summary>
    /// Explains why enrollment is waiting and when the agent should try again.
    /// </summary>
    private sealed record PendingResponse(
        int SchemaVersion,
        Guid RequestId,
        string Status,
        int RetryAfterSeconds);

    /// <summary>
    /// Carries the next check-in time and confirmation that login history was received.
    /// </summary>
    private sealed record CheckinResponse(
        int SchemaVersion,
        Guid DeviceGuid,
        DateTime ServerTimeUtc,
        int CheckinIntervalSeconds,
        bool UserLoginsSubmitted = false,
        CloneDeploy_Agent.Contracts.AgentUpdateOffer? Update = null,
        string? BrandingVersion = null, CloneDeploy_Agent.Contracts.AgentFeatures? Features = null,
        CloneDeploy_Agent.Contracts.AgentCertificateRenewal? CertificateRenewal = null);

    public async Task<AgentCycleResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (identity.State.CertificatePem is null)
        {
            var readiness = WindowsSetupReadiness.ReadForEnrollment();
            if (!readiness.Ready)
                return new(AgentCycleStatus.WaitingForWindowsSetup, TimeSpan.FromSeconds(30))
                    { EnrollmentWaitReason = readiness.Reason };
            identity.RefreshEnrollmentName(Environment.MachineName);
            var pending = await EnrollAsync(cancellationToken);

            if (pending is not null)
            {
                return pending;
            }
        }

        return await CheckInAsync(cancellationToken);
    }


    // A null result means enrollment completed and the agent can check in immediately.
    private async Task<AgentCycleResult?> EnrollAsync(CancellationToken cancellationToken)
    {
        if (options.ProvisioningKey.Length != 64 || !options.ProvisioningKey.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException("Install a configured agent MSI prepared with the current provisioning key.");
        }

        using var client = CreateClient();
        var hardwareUuid = EnrollmentHardware.ReadUuid();
        var macAddresses = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Ethernet
                or System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211)
            .Select(n => n.GetPhysicalAddress().ToString()).Where(mac => mac.Length == 12).Distinct().Take(64).ToArray();
        using var response = await client.PostAsJsonAsync(
            "api/agent/v1/enroll",
            new
            {
                options.ProvisioningKey,
                identity.State.DeviceName,
                identity.State.InstallationId,
                identity.State.CsrPem,
                identity.State.RequestReset,
                HardwareUuid = hardwareUuid,
                MacAddresses = macAddresses
            },
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return RateLimited(response);
        }

        if (response.StatusCode == HttpStatusCode.Conflict && !identity.State.RequestReset)
        {
            return RequestRecovery();
        }

        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            var pending = await response.Content.ReadFromJsonAsync<PendingResponse>(cancellationToken)
                          ?? throw new InvalidDataException("Empty enrollment response.");

            if (pending.SchemaVersion != 1 || pending.RequestId == Guid.Empty)
            {
                throw new InvalidDataException("Unsupported pending enrollment response.");
            }

            var status = pending.Status switch
            {
                "PendingApproval" => AgentCycleStatus.PendingApproval,
                "PendingPreProvision" => AgentCycleStatus.PendingPreProvision,
                "PendingReset" => AgentCycleStatus.PendingReset,
                _ => throw new InvalidDataException("Unsupported pending enrollment status.")
            };

            return new(
                status,
                TimeSpan.FromSeconds(Math.Clamp(
                    pending.RetryAfterSeconds,
                    MinimumCheckInSeconds,
                    MaximumRetrySeconds)));
        }

        RequireSuccess(response, "Enrollment");
        var issued = await response.Content.ReadFromJsonAsync<EnrollmentResponse>(cancellationToken)
                     ?? throw new InvalidDataException("Empty enrollment response.");

        if (issued.SchemaVersion != 1)
        {
            throw new InvalidDataException("Unsupported enrollment response version.");
        }

        identity.AcceptCertificate(issued.DeviceGuid, issued.CertificatePem, issued.CaCertificatePem);

        return null;
    }

    private async Task<AgentCycleResult> CheckInAsync(CancellationToken cancellationToken)
    {
        AgentCertificateLifecycle.ThrowIfExpired(identity.State.CertificatePem);
        X509Certificate2 certificate;

        try
        {
            certificate = identity.GetClientCertificate()
                          ?? throw new InvalidDataException("Missing device certificate.");
        }
        catch (Exception exception) when (exception is InvalidDataException or CryptographicException)
        {
            return RequestRecovery();
        }

        using var ownedCertificate = certificate;
        using var authenticated = CreateClient(certificate);
        LoginTracker?.Sample();
        var logins = UserLogins?.Pending();
        using var checkin = await authenticated.PostAsJsonAsync(
            "api/agent/v1/check-in",
            new
            {
                ComputerName = Environment.MachineName,
                ClientVersion = typeof(AgentClient).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                Architecture = CloneDeploy_Agent.Contracts.WindowsArchitecture.Process,
                CertificateRenewalCsr = identity.PrepareRenewal(), SupportsCertificateRenewal = true,
                UserLogins = logins
            },
            cancellationToken);

        if (checkin.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return RateLimited(checkin);
        }

        if (checkin.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            AgentCertificateLifecycle.ThrowIfExpired(identity.State.CertificatePem);
            return RequestRecovery();
        }

        RequireSuccess(checkin, "Certificate-authenticated check-in");
        var result = await checkin.Content.ReadFromJsonAsync<CheckinResponse>(cancellationToken)
                     ?? throw new InvalidDataException("Empty check-in response.");

        if (result.SchemaVersion != 1 || result.DeviceGuid != identity.State.DeviceGuid)
        {
            throw new InvalidDataException("Check-in response does not match this device.");
        }

        identity.CompleteRenewal();
        if (result.CertificateRenewal is { } renewal)
        {
            identity.AcceptRenewal(renewal);
            http.Invalidate(); // Leased requests can finish with the original certificate.
        }
        new AgentFeatureCache(options).Save(result.Features ?? new());
        var brandingCache = new AgentBrandingCache(options);

        if (result.BrandingVersion is not null && brandingCache.Read().Version != result.BrandingVersion)
        {
            try
            {
                var branding = await authenticated.GetFromJsonAsync<CloneDeploy_Agent.Contracts.AgentBranding>(
                    "api/agent/v1/branding",
                    cancellationToken);

                if (branding is not null)
                    brandingCache.Save(branding);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException or UnauthorizedAccessException
                                       || ex is OperationCanceledException
                                       && !cancellationToken.IsCancellationRequested)
            {
                /* Retain cached branding and retry on the next normal check-in. */
            }
        }

        if (result.UserLoginsSubmitted && logins is not null)
            UserLogins!.Acknowledge(logins);

        return new(
            AgentCycleStatus.CheckedIn,
            TimeSpan.FromSeconds(Math.Clamp(
                result.CheckinIntervalSeconds,
                MinimumCheckInSeconds,
                MaximumCheckInSeconds)),
            result.DeviceGuid)
        {
            Update = result.Update
        };
    }

    private AgentCycleResult RequestRecovery()
    {
        identity.ResetEnrollment(options);
        http.Invalidate();

        return new(AgentCycleStatus.RecoveryRequested, TimeSpan.FromSeconds(1));
    }

    private HttpClient CreateClient(X509Certificate2? certificate = null)
        => http.Create(certificate);

    public void Dispose() => http.Dispose();

    private static AgentCycleResult RateLimited(HttpResponseMessage response)
    {
        var delay = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromMinutes(1);

        return new(
            AgentCycleStatus.RateLimited,
            TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, MinimumCheckInSeconds, MaximumRetrySeconds)));
    }

    private static void RequireSuccess(HttpResponseMessage response, string operation)
    {
        if (response.StatusCode == HttpStatusCode.OK)
        {
            return;
        }

        var guidance = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Check the provisioning key, or whether the enrolled device/certificate is revoked or archived.",
            HttpStatusCode.Forbidden => "Enrollment may have been denied; review Devices > Approval / Reset Requests.",
            HttpStatusCode.Conflict => "This identity conflicts with a server record. Review the existing device; the agent will retain its identity.",
            _ => "Check server availability and configuration."
        };
        throw new HttpRequestException(
            $"{operation} returned HTTP {(int)response.StatusCode}. {guidance}",
            null,
            response.StatusCode);
    }
}
