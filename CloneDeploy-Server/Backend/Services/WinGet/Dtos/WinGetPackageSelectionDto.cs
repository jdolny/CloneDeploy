using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services;

public sealed record WinGetPackageSelectionDto(WinGetPackageVersionDto Package, bool InstallLatest)
{
    public void ApplyTo(SoftwareAction action)
    {
        action.PackageId = Package.PackageIdentifier;
        action.PackageVersion = InstallLatest ? null : Package.Version;
        action.InstallLatest = InstallLatest;
    }
}
