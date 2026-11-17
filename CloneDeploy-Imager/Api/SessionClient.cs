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
/// Authenticates operators, validates sessions, and links hardware inventory to a server device.
/// </summary>
internal sealed partial class ImagingClient
{
    private const int MaximumLoginSessionSeconds = 86400 * 365;

    public async Task<LoginStatus> LoginAsync(string userName, string password, string? authenticatorCode, CancellationToken ct)
    {
        using var deadline = Deadline(ct);
        ct = deadline.Token;
        SignOut();
        using var response = await http.PostAsJsonAsync("api/imaging/login", new { userName, password, authenticatorCode }, ct);
        log.Write($"Imaging login HTTP {(int)response.StatusCode}.");
        if (response.IsSuccessStatusCode)
        {
            var result = await response.Content.ReadFromJsonAsync<SessionToken>(ct);
            if (result is null || !string.Equals(result.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(result.AccessToken) || result.AccessToken.Any(char.IsWhiteSpace) || result.ExpiresIn is <= 0 or > MaximumLoginSessionSeconds)
                throw new InvalidDataException("The server returned an invalid session.");
            token = result.AccessToken;
            expiresAt = DateTimeOffset.UtcNow.AddSeconds(result.ExpiresIn);
            return LoginStatus.Success;
        }
        var error = await ErrorCode(response, ct);
        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized when error == "mfa_required" => LoginStatus.MfaRequired,
            HttpStatusCode.Forbidden when error == "mfa_enrollment_required" => LoginStatus.MfaEnrollmentRequired,
            HttpStatusCode.Unauthorized => LoginStatus.Rejected,
            HttpStatusCode.Forbidden => LoginStatus.Forbidden,
            HttpStatusCode.TooManyRequests => LoginStatus.RateLimited,
            _ => throw new HttpRequestException($"The imaging server returned HTTP {(int)response.StatusCode}.", null, response.StatusCode)
        };
    }

    public async Task<ImagingSession> SessionAsync(CancellationToken ct)
    {
        using var deadline = Deadline(ct);
        ct = deadline.Token;
        if (token is null || DateTimeOffset.UtcNow >= expiresAt) { SignOut(); throw new SessionExpiredException(); }
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/imaging/session");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, ct);
        log.Write($"Imaging session HTTP {(int)response.StatusCode}.");
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        { SignOut(); throw new SessionExpiredException(); }
        response.EnsureSuccessStatusCode();
        var session = await response.Content.ReadFromJsonAsync<ImagingSession>(ct);
        if (session is null || string.IsNullOrWhiteSpace(session.Subject) || session.Kind is not ("user" or "automation"))
            throw new InvalidDataException("The server returned an invalid session.");
        permissions = new(session.Permissions, StringComparer.Ordinal);
        return session;
    }

    public async Task<LinkedImagingDevice?> LinkDeviceAsync(HardwareInventory inventory, string? name, CancellationToken ct)
    {
        if (name is not null)
        {
            name = name.Trim();
            CloneDeploy_Imager.Deployment.OfflineComputerName.Validate(name);
        }
        using var content = JsonContent.Create(new { hardware = new { inventory.Uuid, inventory.Serial, inventory.Manufacturer, inventory.Model,
            macAddresses = inventory.Networks.Select(x => x.MacAddress).Distinct().ToArray(),
            networks = inventory.Networks.Select(x => new { Mac = x.MacAddress, x.IpNetworks }).ToArray() }, name });
        using var response = await UploadRequest(HttpMethod.Post, "device", content, ct);
        var result = await response.Content.ReadFromJsonAsync<DeviceLinkResponse>(ct) ?? throw new InvalidDataException("Missing device lookup result.");
        if (result.Device is { } device && (device.Id <= 0 || string.IsNullOrWhiteSpace(device.Name) || string.IsNullOrWhiteSpace(device.LinkToken)))
            throw new InvalidDataException("Invalid device link.");
        Device = result.Device;
        return Device;
    }

    public async Task RenameDeviceAsync(string name, CancellationToken ct)
    {
        name = name.Trim();
        CloneDeploy_Imager.Deployment.OfflineComputerName.Validate(name);
        var device = Device ?? throw new InvalidOperationException("Link this session to a device first.");
        using var content = JsonContent.Create(new { deviceLinkToken = device.LinkToken, name });
        using var response = await UploadRequest(HttpMethod.Post, "device/rename", content, ct);
        Device = device with { Name = name };
    }
}
