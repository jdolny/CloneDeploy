namespace CloneDeploy_Server.Backend.Data.Models;

public sealed class AgentRequestNonce
{
    public string Id { get; set; } = "";
    public DateTime ExpiresAtUtc { get; set; }
}
