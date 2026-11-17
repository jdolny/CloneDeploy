namespace CloneDeploy_Server.Backend.Services;

public sealed record ActionFileItemDto(
    int Id,
    bool External,
    string Name,
    string? Url,
    long? Size,
    string Status,
    string? Progress,
    string? Error,
    string? Sha256,
    DateTime? UpdatedAtUtc,
    string? ExpectedSha256,
    bool Busy)
{
    public bool AutoRefresh { get; init; }
    public DateTime? LastCheckedAtUtc { get; init; }
}