using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.Backend.Services.DynamicGroups;

public sealed record DeviceGroupListItemDto(int Id, string Name, DeviceGroupType Type, int Members, DateTime? EvaluatedAt)
{
    public List<int> TagIds { get; init; } = [];
    public bool CanDelete => Type is DeviceGroupType.Manual or DeviceGroupType.Dynamic;
}
