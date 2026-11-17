using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services;

public sealed record WinGetPackageVersionDto(string PackageIdentifier, string Version, string Name, string? Publisher,
    string? Description, string? License, string Scope);

