using System.Text.Json;

namespace CloneDeploy_Imager.Configuration;

/// <summary>
/// Loads and validates runtime settings, including the server address, tool paths, and request timeout.
/// </summary>
internal sealed record ImagerSettings
{
    private const int MaximumRequestTimeoutSeconds = 300;

    public string? ServerUrl { get; init; }
    // Older WinPE builders included bootstrap architecture in the runtime settings.
    // Accept it for compatibility; executable architecture is selected by the bootstrap.
    public string? Architecture { get; init; }
    public string ServerCaCertificatePath { get; init; } = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "CloneDeploy", "CloneDeploy-Root-CA.cer");
    public string LogDirectory { get; init; } = Path.Combine(Path.GetTempPath(), "CloneDeploy-Imager", "logs");
    public int RequestTimeoutSeconds { get; init; } = 30;
    public string? FfuScratchDirectory { get; init; }
    public string WimlibPath { get; init; } = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "CloneDeploy", "Tools", "wimlib", "wimlib-imagex.exe");

    public static ImagerSettings Load(string? file)
    {
        var path = file ?? Path.Combine(AppContext.BaseDirectory, "imager.json");
        if (!File.Exists(path))
        {
            if (file is not null) throw new FileNotFoundException("The specified configuration file was not found.", path);
            return new();
        }
        var settings = JsonSerializer.Deserialize<ImagerSettings>(File.ReadAllText(path), new JsonSerializerOptions
        { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })
            ?? throw new ArgumentException("Configuration must be a JSON object.");
        if (settings.ServerUrl is not null) _ = ServerAddress(settings.ServerUrl);
        if (settings.Architecture is not null and not ("x64" or "arm64"))
            throw new ArgumentException("Architecture must be x64 or arm64 when supplied.");
        if (settings.RequestTimeoutSeconds is < 1 or > MaximumRequestTimeoutSeconds) throw new ArgumentException("RequestTimeoutSeconds must be between 1 and 300.");
        if (string.IsNullOrWhiteSpace(settings.LogDirectory)) throw new ArgumentException("LogDirectory cannot be empty.");
        if (string.IsNullOrWhiteSpace(settings.ServerCaCertificatePath)) throw new ArgumentException("ServerCaCertificatePath cannot be empty.");
        if (settings.FfuScratchDirectory is not null && (string.IsNullOrWhiteSpace(settings.FfuScratchDirectory) || !Path.IsPathFullyQualified(settings.FfuScratchDirectory)))
            throw new ArgumentException("FfuScratchDirectory must be an absolute local directory.");
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        return settings with { LogDirectory = Path.GetFullPath(settings.LogDirectory, directory),
            WimlibPath = Path.GetFullPath(settings.WimlibPath, directory),
            ServerCaCertificatePath = Path.GetFullPath(settings.ServerCaCertificatePath, directory) };
    }

    public static Uri ServerAddress(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Enter the HTTPS application address without credentials, a query, or a fragment.");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
    }
}

/// <summary>
/// Parses supported command-line options and rejects unknown or incomplete arguments.
/// </summary>
internal sealed record LaunchOptions(string? ConfigPath, bool Plain, bool Inventory)
{
    public static LaunchOptions Parse(string[] args)
    {
        string? config = null;
        bool plain = false, inventory = false;
        for (var i = 0; i < args.Length; i++)
            switch (args[i])
            {
                case "--config" when i + 1 < args.Length && !args[i + 1].StartsWith("--"): config = args[++i]; break;
                case "--plain": plain = true; break;
                case "--inventory": inventory = true; break;
                default: throw new ArgumentException("Unknown or incomplete argument: " + args[i]);
            }
        return new(config, plain, inventory);
    }
}
