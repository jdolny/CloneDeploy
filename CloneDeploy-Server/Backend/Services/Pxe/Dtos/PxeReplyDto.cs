using System.Net;

namespace CloneDeploy_Server.Backend.Services;

public sealed record PxeReplyDto(byte[] Data, IPEndPoint Destination)
{
    public string? BootFile { get; init; }
    public string? Architecture { get; init; }
    public string? Reason { get; init; }
}
