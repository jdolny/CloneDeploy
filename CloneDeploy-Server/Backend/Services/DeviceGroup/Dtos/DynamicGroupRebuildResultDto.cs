namespace CloneDeploy_Server.Backend.Services.DynamicGroups;

public sealed record DynamicGroupRebuildResultDto(int GroupId, int Members, DateTime EvaluatedAtUtc);
