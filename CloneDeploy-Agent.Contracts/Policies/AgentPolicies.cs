using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace CloneDeploy_Agent.Contracts;

// Policy remains raw JSON so its published SHA-256 can be checked before execution.
public sealed record AgentPolicySnapshot(int SchemaVersion, string Hash, bool ReRunExisting, JsonElement Policy);
public sealed record AgentPolicyStart(Guid RunId, int PolicyId, [Required, StringLength(64)] string Hash,
    [StringLength(256)] string? UserName = null)
{
    [StringLength(184)] public string? UserSid { get; init; }
    public bool SelfService { get; init; }
    public bool SupportsNotificationRecipients { get; init; }
}
public sealed record AgentPolicyReceipt(Guid RunId, bool Completed)
{
    // Null means unrestricted; an empty array deliberately addresses nobody.
    public string[]? NotificationRecipientSids { get; init; }
}
public sealed record AgentPolicyResult(Guid RunId, int Result, [Required, StringLength(16000)] string Detail);
public sealed record AgentPolicyCredential(string Username, string Password);

public sealed record AgentPolicyContent(int Id, Guid Guid, string Name, int ExecutionPriority,
    JsonElement Settings, JsonElement? StartWindow, JsonElement? EndWindow,
    AgentPolicyAction? Condition, List<AgentPolicyAction> Actions)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public long ExecutionRevision { get; init; }
}
public sealed record AgentPolicyAction(Guid Guid, string Name, int Type, int Order,
    JsonElement Settings, AgentPolicyAction? Condition, int ConditionFailedAction,
    Guid? ConditionNextAction, List<AgentPolicyFile> Files);
public sealed record AgentPolicyFile(int Id, Guid DirectoryGuid, string Name, long Size, string Sha256Hash);
