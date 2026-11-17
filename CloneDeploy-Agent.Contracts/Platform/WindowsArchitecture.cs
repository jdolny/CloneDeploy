namespace CloneDeploy_Agent.Contracts;

public static class WindowsArchitecture
{
    public const string X64UpgradeCode = "{D75325AF-F72B-4039-9B5D-DBAA9E8AB0F5}";
    public const string Arm64UpgradeCode = "{620CDB5F-90AA-4381-9FAA-6546CBC2E350}";
    public static string Require(string architecture) => architecture is "x64" or "arm64" ? architecture
        : throw new InvalidDataException("Supported Windows architectures are x64 and arm64.");
    public static string Process => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => "x64",
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        _ => throw new PlatformNotSupportedException("This application requires x64 or ARM64 Windows.")
    };
    public static string UpgradeCode(string architecture) => Require(architecture) == "arm64" ? Arm64UpgradeCode : X64UpgradeCode;
}
