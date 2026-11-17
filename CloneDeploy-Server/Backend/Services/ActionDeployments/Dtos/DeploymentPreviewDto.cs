using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services;

public sealed record DeploymentPreviewDto(string ActionName, string Hash, string ConfirmationHash, int Total, List<PolicyTargetDeviceDto> Devices);