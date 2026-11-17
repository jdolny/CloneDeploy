namespace CloneDeploy_Agent.MacOS;

public sealed record MacConfiguration
{
    private const int MaximumDeviceNameCharacters = 128;

    public string ServerUrl { get; init; } = "";
    public string ProvisioningKey { get; init; } = "";
    public string DeviceName { get; init; } = Environment.MachineName;
    public string? ServerCaCertificatePath { get; init; }
    internal AgentOptions Options(string directory)
    {
        if (string.IsNullOrWhiteSpace(DeviceName) || DeviceName.Length > MaximumDeviceNameCharacters || DeviceName.Any(char.IsControl))
            throw new InvalidDataException("Device name must contain 1–128 printable characters.");
        if (ProvisioningKey.Length != 64 || !ProvisioningKey.All(Uri.IsHexDigit))
            throw new InvalidDataException("The enrollment provisioning key must contain 64 hexadecimal characters.");
        var options = new AgentOptions { ServerUrl = ServerUrl, ProvisioningKey = ProvisioningKey, DeviceName = DeviceName,
            StateDirectory = directory, ServerCaCertificatePath = ServerCaCertificatePath };
        options.ServerUrl = options.GetServerUri().AbsoluteUri;
        AgentServerTrust.ValidateConfiguration(options);
        return options;
    }
}

public sealed record MacIdentityState
{
    public int SchemaVersion { get; init; } = 1;
    public required string ServerUrl { get; init; }
    public required string DeviceName { get; init; }
    public Guid InstallationId { get; init; } = Guid.NewGuid();
    public required string PrivateKeyPkcs8 { get; init; }
    public required string CsrPem { get; init; }
    public Guid? DeviceGuid { get; init; }
    public string? CertificatePem { get; init; }
    public string? CaCertificatePem { get; init; }
    public bool RequestReset { get; init; }
    public string? PendingPrivateKeyPkcs8 { get; init; }
    public string? PendingCsrPem { get; init; }
    public string? PreviousPrivateKeyPkcs8 { get; init; }
}
