namespace CloneDeploy_Agent;

/// <summary>
/// Holds the saved progress and result of one deployment task.
/// </summary>
public sealed record DeviceTaskRun(
    Guid TaskId,
    Guid LeaseToken,
    Guid DeviceGuid,
    string State,
    DateTime StartedAtUtc,
    DateTime? FinishedAtUtc = null,
    bool? Success = null,
    string? Detail = null,
    bool Reported = false);