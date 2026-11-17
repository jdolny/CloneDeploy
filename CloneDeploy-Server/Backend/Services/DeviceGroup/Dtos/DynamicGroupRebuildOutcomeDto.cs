namespace CloneDeploy_Server.Backend.Services.DynamicGroups;

public sealed record DynamicGroupRebuildOutcomeDto(DynamicGroupRebuildResultDto? Result, string? Error);
