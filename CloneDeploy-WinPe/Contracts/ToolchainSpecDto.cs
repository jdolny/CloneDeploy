namespace CloneDeploy_Server.WinPe;

public sealed record ToolchainSpecDto(string Name, string Version, string AdkUrl, string AdkSha256, string WinPeUrl, string WinPeSha256)
{
    public static ToolchainSpecDto MicrosoftDefault => new("Microsoft ADK / WinPE September 2026", "10.1.26100.9457",
        "https://download.microsoft.com/download/8e0c0f5a-abb5-4358-a51b-168eb40b1590/adk/adksetup.exe",
        "ac6a930fdb5c2980ba5fefe606d47edaafcf5f647b4337411500d158ea77300f",
        "https://download.microsoft.com/download/a4a79e7a-f085-41c4-aebf-2538fd000790/adkwinpeaddons/adkwinpesetup.exe",
        "d4de67acc83de253ccc941dd0590808d83726bdd8ea768a374a7beaff7949d6a");
}
