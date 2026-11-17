namespace CloneDeploy_Server.Backend.Services;

public sealed record DirectoryUserHistoryRow(Guid RunId, int PolicyId, string PolicyName, int DeviceId, string DeviceName,
    string? UserName, DateTime StartedAtUtc, DateTime? FinishedAtUtc, int? Result, string? Detail, string Hash);
public sealed record DirectoryUserHistoryPage(int Total, List<DirectoryUserHistoryRow> Rows, bool HasSid);
