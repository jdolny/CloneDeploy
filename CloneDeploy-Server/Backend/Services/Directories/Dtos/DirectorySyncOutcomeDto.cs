namespace CloneDeploy_Server.Backend.Services;

// Internal job outcome. A summary is returned only after the transaction commits.
public sealed record DirectorySyncOutcomeDto(DirectorySyncSummaryDto? Summary, string? Error);

