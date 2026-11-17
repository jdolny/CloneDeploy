using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services;

public sealed record DeploymentSettingsDto(
    int ImageId,
    Guid ImageVersionId,
    int StorageProviderId,
    string ImagePath,
    int DeploymentProfileId,
    string ProfileName,
    int? DriverPackId,
    string ComputerName)
{
    public int? OuNamingRuleSetId { get; init; }
    public DeploymentOuPrompt OuPrompt { get; init; }
    public bool AutomaticDrivers { get; init; }
    public bool SetComputerName { get; init; }
    public DeploymentCompletionAction AfterDeployment { get; init; }
    public bool DriversResolved { get; init; }
    public string? DriverWarning { get; init; }
    public List<DeploymentDriverArchiveDto> Drivers { get; init; } = [];
}

public sealed record DeploymentDriverArchiveDto(Guid Id, int? StorageProviderId, string Sha256, string Name);
