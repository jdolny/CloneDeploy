namespace CloneDeploy_Agent.Contracts;

public static class AgentPlatform
{
    public const string Windows = "Windows";
    public const string MacOS = "macOS";
    public static bool Supported(string? platform, string? architecture) =>
        platform == Windows && architecture is "x64" or "arm64" || platform == MacOS && architecture == "arm64";
}
