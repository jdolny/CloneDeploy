namespace CloneDeploy_Agent;

/// <summary>
/// Holds the saved details that let the server recognize this agent after a restart.
/// </summary>
public sealed record IdentityState(
    int SchemaVersion,
    string ServerUrl,
    string DeviceName,
    Guid InstallationId,
    string KeyName,
    string? CsrPem = null,
    Guid? DeviceGuid = null,
    string? CertificatePem = null,
    string? CaCertificatePem = null,
    bool RequestReset = false,
    string? PendingKeyName = null,
    string? PendingCsrPem = null,
    string? PreviousKeyName = null);