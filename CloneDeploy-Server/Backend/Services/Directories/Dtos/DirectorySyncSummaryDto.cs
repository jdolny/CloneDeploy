namespace CloneDeploy_Server.Backend.Services;

public sealed record DirectorySyncSummaryDto(DateTime CompletedAtUtc, int Objects, int Missing);

