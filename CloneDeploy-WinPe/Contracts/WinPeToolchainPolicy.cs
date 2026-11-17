namespace CloneDeploy_Server.WinPe;

/// <summary>Shared by the application and builder; changing kits requires a new release of both.</summary>
public static class WinPeToolchainPolicy
{
    public const int Protocol = 7;

    public static bool SupportsApplication(SetupHealthDto health) =>
        health.Protocol == Protocol && health.RequiredVersion == ToolchainSpecDto.MicrosoftDefault.Version;

    public static bool InstalledVersionsMatch(SetupHealthDto health) =>
        health.AdkVersion == ToolchainSpecDto.MicrosoftDefault.Version &&
        health.WinPeVersion == ToolchainSpecDto.MicrosoftDefault.Version;

    public static void ValidateRequest(ToolchainSpecDto spec)
    {
        if (spec != ToolchainSpecDto.MicrosoftDefault)
            throw new InvalidDataException("This setup request does not match the ADK/WinPE prerequisites required by this application release. Update the application and builder together and queue setup again.");
    }

    public static void ValidateBuildVersion(string? version)
    {
        if (version != ToolchainSpecDto.MicrosoftDefault.Version)
            throw new InvalidDataException("This build was queued for a different or unspecified ADK/WinPE version. Update the application and builder together and queue a new build.");
    }
}
