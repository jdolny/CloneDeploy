using System.Security.Claims;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Retrieves diagnostic logs from a connected device agent.
/// </summary>
public sealed class AgentLogService(ICloneDeployDbFactory factory, AgentConnectionService connections,
    AgentServerInstance server, IHubContext<AgentHub> hub, IAuthorizationService authorization, TimeProvider clock,
    ILogger<AgentLogService> logger)
{
    public async Task<AgentLogReply> ReadAsync(ClaimsPrincipal user, int deviceId, CancellationToken ct = default, Guid? downloadId = null, int offset = 0)
    {
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.DeviceView)).Succeeded)
            throw new UnauthorizedAccessException("Device viewing permission is required to view agent logs.");
        var id = Guid.NewGuid();
        var connection = await connections.FindActiveAsync(deviceId, ct);
        if (connection is null) return new(id, false, "", "The agent is offline.");
        if (connection.ServerInstanceId != server.Id)
            return new(id, false, "", "The agent is connected to another server instance. Open its log on that instance.");
        await using var db = await factory.CreateContextAsync();
        var deviceGuid = await db.Devices.Where(x => x.Id == deviceId && x.ArchivedAtUtc == null)
            .Select(x => (Guid?)x.Guid).SingleOrDefaultAsync(ct);
        if (deviceGuid is null) return new(id, false, "", "The device is unavailable.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var reply = await hub.Clients.Client(connection.ConnectionId).InvokeAsync<AgentLogReply>(
                AgentHubProtocol.ReadLog, new AgentLogRequest(id, deviceGuid.Value, clock.GetUtcNow().AddSeconds(15)) { DownloadId = downloadId, Offset = offset }, timeout.Token);
            if (reply is null || reply.RequestId != id || reply.Text is null || reply.Text.Length > AgentLogReply.MaxCharacters
                || reply.Data is { Length: > AgentLogReply.ChunkBytes } || reply.TotalBytes < 0 || reply.TotalBytes > AgentLogReply.MaxDownloadBytes
                || reply.Detail is null || reply.Detail.Length > 256)
                return new(id, false, "", "The agent returned an invalid log response.");
            return reply;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is OperationCanceledException or HubException or IOException)
        {
            logger.LogDebug("Agent log request for device {DeviceId} did not complete ({ErrorType})", deviceId, ex.GetType().Name);
            return new(id, false, "", "The agent did not return its log. It may need an update or reconnection.");
        }
    }
    public async Task<byte[]> DownloadAsync(ClaimsPrincipal user, int deviceId, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var downloadId = Guid.NewGuid();
        using var output = new MemoryStream();
        int? total = null;
        do
        {
            var reply = await ReadAsync(user, deviceId, timeout.Token, downloadId, (int)output.Length);
            if (!reply.Available) throw new IOException(reply.Detail);
            if (reply.Data is null)
                throw new IOException("Full log downloads require an updated agent.");
            total ??= reply.TotalBytes;
            if (total != reply.TotalBytes || output.Length + reply.Data.Length > total
                || (reply.Data.Length == 0 && output.Length != total))
                throw new IOException("The agent returned an invalid log download.");
            output.Write(reply.Data);
        } while (output.Length < total);
        return output.ToArray();
    }

}
