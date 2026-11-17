
namespace CloneDeploy_Server.Backend.Services;

public sealed record DriverMatchRuleDto(int ProfileId, string Name, string? ManufacturerRegex, string? ModelRegex, List<InventoriedModelDto> Models);

