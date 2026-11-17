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

internal enum LoginStatus { Success, MfaRequired, MfaEnrollmentRequired, Rejected, Forbidden, RateLimited }
/// <summary>
/// Identifies the authenticated imaging subject and whether the session belongs to a user or automation.
/// </summary>
internal sealed record ImagingSession(string? Subject, string? Name, string? Kind) { public string[] Permissions { get; init; } = []; }
/// <summary>
/// Carries a bearer token and its lifetime returned by imaging authentication.
/// </summary>
internal sealed record SessionToken(string TokenType, string AccessToken, int ExpiresIn);
/// <summary>
/// Identifies an assigned deployment and the device, image version, and profile selected by the server.
/// </summary>
internal sealed record DeploymentAssignment(Guid DeploymentId, int DeviceId, string DeviceName, Guid ImageVersionId,
    string ImageName, string ProfileName, DateTime CreatedAtUtc)
{
    public bool AgentInitiated { get; init; }
}
/// <summary>
/// Combines deployment-scoped credentials with the assignment they authorize.
/// </summary>
internal sealed record DeploymentSession(string TokenType, string AccessToken, int ExpiresIn, DeploymentAssignment Assignment);
/// <summary>
/// Signals that the server could not claim a unique, available deployment for this device.
/// </summary>
internal sealed class DeploymentClaimConflictException(string message) : Exception(message);
/// <summary>
/// Describes an image destination offered for WIM or FFU capture.
/// </summary>
internal sealed record CaptureImage(int Id, string Name, string Format = "Wim");
/// <summary>
/// Provides an SMB destination and credentials while redacting them from its string representation.
/// </summary>
internal sealed record SmbCapture(string Root, string RelativePath, string? Username = null, string? Password = null)
{
    public override string ToString() => "SMB capture connection (credentials redacted)";
}
/// <summary>
/// Describes a reserved upload, its chunk limit, and the selected FFU transport or SMB destination.
/// </summary>
internal sealed record CaptureUpload(Guid Id, int ChunkBytes, string Format = "Wim", SmbCapture? Direct = null)
{
    public string? FfuTransport { get; init; }
}
/// <summary>
/// Identifies the published image version and whether it became the active version.
/// </summary>
internal sealed record CaptureResult(Guid Id, int Number, bool IsActive);
/// <summary>
/// Identifies the server device linked to this session and supplies its device-link token.
/// </summary>
internal sealed record LinkedImagingDevice(int Id, string Name, string LinkToken, bool Created);
/// <summary>
/// Wraps a device lookup or registration result, which is empty when registration is needed.
/// </summary>
internal sealed record DeviceLinkResponse(LinkedImagingDevice? Device);

