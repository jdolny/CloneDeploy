using System.Text.Json;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.Backend.Services;

public sealed record PolicyTargetKeyDto(int? GroupId, int? DirectorySourceId, Guid? OuGuid)
{
    public int? DeviceId { get; init; }
    public bool IsDevice => DeviceId.HasValue;
    public bool IsOu => GroupId is null && DeviceId is null;
    public static PolicyTargetKeyDto Device(int id) => new(null, null, null) { DeviceId = id };
    public static PolicyTargetKeyDto Group(int id) => new(id, null, null);
    public static PolicyTargetKeyDto Ou(int sourceId, Guid guid) => new(null, sourceId, guid);

    public bool IsValid => DeviceId is > 0 && GroupId is null && DirectorySourceId is null && OuGuid is null ||
                           DeviceId is null && (GroupId is > 0 && DirectorySourceId is null && OuGuid is null ||
                           GroupId is null && DirectorySourceId is > 0 && OuGuid.HasValue && OuGuid != Guid.Empty);
}