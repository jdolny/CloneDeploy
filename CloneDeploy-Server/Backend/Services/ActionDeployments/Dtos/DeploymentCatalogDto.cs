using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services;

public sealed record DeploymentCatalogDto(List<PolicyTargetOptionDto> Targets, List<PolicyTargetDirectoryNodeDto> DirectoryNodes);