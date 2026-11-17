namespace CloneDeploy_Agent.Contracts;

/// <summary>A device-wide notice retained for display by each interactive user's portal.</summary>
public enum NotificationPosition { LowerRight = 0, Center = 1 }
public sealed record AgentPortalNotification(string Id, DateTimeOffset CreatedAt, string Title, string Body, int TimeoutSeconds)
{
    public bool RequireAcknowledgment { get; init; }
    public bool RemindAtLogin { get; init; }
    public NotificationPosition Position { get; init; }
    public NotificationBranding Branding { get; init; }
    public NotificationKind Kind { get; init; }
    public DateTimeOffset? AcknowledgedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public bool IsExpired => ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow;
    public bool PendingAcknowledgment => RequireAcknowledgment && AcknowledgedAt is null && !IsExpired;
}
public sealed record AgentNotificationAcknowledgment(string NotificationId, string UserSid, DateTimeOffset AcknowledgedAt);
public sealed record AgentNotificationReceipt(AgentPortalNotification Notification, string UserSid, string UserName, Guid Revision);


/// <summary>Read-only policy metadata safe to display to a device's interactive Windows users.</summary>
public sealed record AgentPortalPolicy(int Id, string Name, int Priority)
{
    public string? Description { get; init; }
    public string? Icon { get; init; }
    public string? IconHash { get; init; }
    public AgentPortalRun? LastRun { get; init; }
    public AgentPortalCategory? Category { get; init; }
}

public sealed record AgentPortalRun(DateTime StartedAtUtc, DateTime? FinishedAtUtc, int? Result);

public sealed record AgentPortalCategory(int Id, string Name, string Icon, int SortOrder);

/// <summary>Stable icon keys; clients map them to local artwork, never executable markup or URLs.</summary>
public static class PortalCategoryIcons
{
    public static IReadOnlyList<string> Keys { get; } = Array.AsReadOnly(new[]
    {
        "Software", "Printers", "Maintenance", "Tools", "Network", "Security", "Help",
        "Productivity", "Communication", "Files & Storage", "Updates", "Accounts", "Education"
    });
}

/// <summary>Local portal response; never contains policy actions, credentials, or enrollment secrets.</summary>
public sealed record AgentPortalSnapshot(string UserName, string DeviceName, string ServerUrl,
    AgentPortalPolicy[] Policies, string? Error = null, AgentPortalIdentity? DirectoryIdentity = null)
{
    public AgentPortalPolicy[] AvailablePolicies { get; init; } = [];
    public string? AgentVersion { get; init; }
}

/// <summary>The result of matching a Windows SID to a synced directory user; does not grant console access.</summary>
public sealed record AgentPortalIdentity(string Status, AgentDirectoryUser? User = null);

/// <summary>Public directory identity metadata for the current portal user.</summary>
public sealed record AgentDirectoryUser(int DirectorySourceId, Guid ObjectGuid, string Name,
    string? UserPrincipalName, string DirectoryName);
