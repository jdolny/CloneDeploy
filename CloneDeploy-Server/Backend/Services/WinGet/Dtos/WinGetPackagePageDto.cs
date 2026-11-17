using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services;

public sealed record WinGetPackagePageDto(int Total, IReadOnlyList<WinGetPackageSummaryDto> Items);
