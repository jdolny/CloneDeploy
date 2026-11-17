namespace CloneDeploy_Agent.Contracts;

public sealed record AgentLogRequest(Guid RequestId, Guid DeviceGuid, DateTimeOffset ExpiresAtUtc)
{
    public Guid? DownloadId { get; init; }
    public int Offset { get; init; }
}
public sealed record AgentLogReply(Guid RequestId, bool Available, string Text, string Detail)
{
    public const int MaxCharacters = 6000;
    public const int ChunkBytes = 24 * 1024;
    public const int MaxDownloadBytes = 5 * 1024 * 1024;
    public byte[]? Data { get; init; }
    public int TotalBytes { get; init; }
}
