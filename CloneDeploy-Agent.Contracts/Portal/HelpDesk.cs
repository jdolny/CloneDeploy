namespace CloneDeploy_Agent.Contracts;

public enum HelpTicketStatus { New, InProgress, WaitingOnUser, Closed }
public enum HelpOperation { Count, List, Read, MarkRead, Create, Reply, Screenshot, Reopen }
public sealed record HelpRequest(HelpOperation Operation, int TicketId = 0, string? Subject = null, string? Body = null,
    Guid CommandId = default, byte[]? Screenshot = null, long MessageId = 0, int Offset = 0, HelpTicketStatus? StatusFilter = null);
public sealed record HelpAgentRequest(string Sid, HelpRequest Request);
public sealed record HelpTicketSummary(int Id, string Subject, HelpTicketStatus Status, DateTime UpdatedAtUtc,
    string Requester, int DeviceId, string DeviceName, string? AssignedTo, bool Unread, int? AssignedToId = null)
{
    public int? CategoryId { get; init; }
    public string? Category { get; init; }
    public int? QueueId { get; init; }
    public string? Queue { get; init; }
    public string? RoutingSummary { get; init; }
}
public sealed record HelpMessage(long Id, string Author, string Body, DateTime CreatedAtUtc, bool FromStaff, bool HasScreenshot);
public sealed record HelpResponse(string? Error = null)
{
    public bool CanCreate { get; init; }
    public string? ScopeMessage { get; init; }
    public string? Identity { get; init; }
    public int UnreadCount { get; init; }
    public long LatestUpdate { get; init; }
    public HelpTicketSummary[] Tickets { get; init; } = [];
    public HelpTicketSummary? Ticket { get; init; }
    public HelpMessage[] Messages { get; init; } = [];
    public bool HasMore { get; init; }
    public byte[]? Screenshot { get; init; }
}

public static class HelpAttachmentLimits
{
    public const int ScreenshotBytes = 4 * 1024 * 1024;
    public const int MaximumDimension = 4096;
    // Base64 image plus message and JSON overhead.
    public const int PayloadBytes = 6 * 1024 * 1024;
}
