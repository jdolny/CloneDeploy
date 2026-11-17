namespace CloneDeploy_Agent;

/// <summary>
/// Holds the settings the agent needs to connect to the server and run on this device.
/// </summary>
public sealed class AgentOptions
{
    internal const string WindowsServiceName = "CloneDeployAgent";
    internal const int MaximumDeviceNameLength = 128;

    public string ServerUrl { get; set; } = "";

    public string ProvisioningKey { get; set; } = "";

    public string DeviceName { get; set; } = Environment.MachineName;

    public string StateDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "CloneDeploy",
        "Agent");

    public string? ServerCaCertificatePath { get; set; }

    public bool RunOnce { get; set; }

    // Optional development-machine log reader; never grants access to agent credentials.
    public string? LogReaderSid { get; set; }

    public Uri GetServerUri()
    {
        if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https"
                                                                     ||
                                                                     uri.UserInfo.Length != 0
                                                                     || uri.Query.Length != 0
                                                                     || uri.Fragment.Length != 0)
        {
            throw new InvalidOperationException("Agent:ServerUrl must be an HTTPS URL without credentials, query, or fragment.");
        }

        return new Uri($"{uri.AbsoluteUri.TrimEnd('/')}/");
    }
}