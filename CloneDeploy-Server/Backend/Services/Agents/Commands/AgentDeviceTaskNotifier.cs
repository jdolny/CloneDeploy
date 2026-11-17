using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Alerts connected devices when they have tasks waiting.
/// </summary>
public sealed class AgentDeviceTaskNotifier(ICloneDeployDbFactory factory, AgentConnectionService connections,
    AgentServerInstance server, IHubContext<AgentHub> hub) : IDeviceTaskNotifier
{
    public Guid ServerInstanceId => server.Id;

    public async Task<bool> NotifyAsync(Guid deviceGuid, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateContextAsync();
        var id = await db.Devices.Where(x => x.Guid == deviceGuid && x.ArchivedAtUtc == null)
            .Select(x => (int?)x.Id).SingleOrDefaultAsync(cancellationToken);
        if (id is null) return false;
        var connection = await connections.FindActiveAsync(id.Value, cancellationToken);
        if (connection?.ServerInstanceId != server.Id) return false;
        await hub.Clients.Client(connection.ConnectionId).SendAsync(AgentHubProtocol.TasksAvailable, cancellationToken);
        return true;
    }
}
