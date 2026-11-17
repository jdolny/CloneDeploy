namespace CloneDeploy_Server.Backend.Services;

// Each process owns its SignalR sockets. The database records presence, not the sockets themselves.
/// <summary>
/// Identifies the running server that owns each live agent connection.
/// </summary>
public sealed class AgentServerInstance
{
    public Guid Id { get; } = Guid.NewGuid();
}

