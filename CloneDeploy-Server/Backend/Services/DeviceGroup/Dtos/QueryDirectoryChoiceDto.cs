using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.Backend.Services.DynamicGroups;

public sealed record QueryDirectoryChoiceDto(int DirectorySourceId, Guid ObjectGuid, DirectoryObjectKind Kind, string Label)
{
    public string Path { get; init; } = "";
    public string DistinguishedName { get; init; } = "";
}
