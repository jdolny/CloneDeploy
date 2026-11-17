using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.Backend.Services;

public sealed record StorageSiteDto(Guid Id, string Name, string? HostOs = null);

/// <summary>
/// Holds storage location details and the selected credential shown when editing them.
/// </summary>
public sealed class StorageProviderDto
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string? LinuxMountPath { get; init; }
    public StorageProviderType Type { get; init; }
    public bool IsEnabled { get; init; }
    public bool ImagingDirectConnect { get; init; }
    public string? WinPeSmbPath { get; init; }
    public bool IsImagingDefault { get; init; }
    public bool IsManagementDefault { get; init; }
    public StoragePurpose Purposes { get; init; } = StoragePurpose.All;
    public string PreferredClientNetworks { get; init; } = "";
    public Guid? SiteServiceId { get; init; }
    public string? SiteServiceName { get; init; }
    public int? ManagedCredentialId { get; init; }
    public string? ManagedCredentialName { get; init; }
}
