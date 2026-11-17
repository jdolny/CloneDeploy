using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services;

public sealed record WinGetPackageSummaryDto(string PackageIdentifier, string Name, string? Publisher);
