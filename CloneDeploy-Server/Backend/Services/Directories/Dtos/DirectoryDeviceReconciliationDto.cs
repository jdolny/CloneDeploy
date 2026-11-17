using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services;

public sealed record DirectoryDeviceReconciliationDto(IReadOnlyList<Device> Additions, IReadOnlyList<string> Conflicts);
