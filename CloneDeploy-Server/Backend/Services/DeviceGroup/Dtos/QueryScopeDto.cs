namespace CloneDeploy_Server.Backend.Services.DynamicGroups;

public sealed record QueryScopeDto(string Id, string Label, Type Type, string? Navigation, bool Collection, string[] Fields);
