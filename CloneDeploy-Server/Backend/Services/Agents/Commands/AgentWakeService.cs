using System.Security.Claims;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using CloneDeploy_Server.Backend.Services.DynamicGroups;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed record WakeResult(int Requested, int Sent)
{
    public IReadOnlyCollection<int> SentDeviceIds { get; init; } = [];
    public string Detail => Requested == 0 ? "No devices to wake up." : Sent == Requested
        ? $"Wake-up packets sent for {Sent} device(s)."
        : $"Wake-up packets sent for {Sent} of {Requested} device(s). The remaining devices have no usable network inventory or available relay.";
}

public sealed class AgentWakeService(ICloneDeployDbFactory factory, AgentConnectionService connections,
    AgentServerInstance server, IHubContext<AgentHub> hub, IAuthorizationService authorization, TimeProvider clock)
{
    public Task<WakeResult> DeviceAsync(ClaimsPrincipal user, int deviceId, CancellationToken ct = default) => RunAsync(user, deviceId, null, ct);
    public Task<WakeResult> GroupAsync(ClaimsPrincipal user, int groupId, CancellationToken ct = default) => RunAsync(user, null, groupId, ct);
    internal Task<WakeResult> DevicesAsync(ClaimsPrincipal user, int[] deviceIds, CancellationToken ct) => RunAsync(user, null, null, ct, deviceIds);

    private async Task<WakeResult> RunAsync(ClaimsPrincipal user, int? deviceId, int? groupId, CancellationToken ct, int[]? deviceIds = null)
    {
        if (!(await authorization.AuthorizeAsync(user, groupId.HasValue || deviceIds is not null
                ? Constants.Permissions.DeviceGroupPowerManagement : Constants.Permissions.DeviceWake)).Succeeded ||
            !int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) throw new UnauthorizedAccessException();
        await using var db = await factory.CreateContextAsync();
        var devices = db.Devices.AsNoTracking().Where(x => x.ArchivedAtUtc == null && x.ProvisionStatus != DeviceProvisionStatus.Archived);
        if (groupId is { } group)
        {
            if (!await db.DeviceGroups.AnyAsync(x => x.Id == group, ct)) throw new InvalidOperationException("Group not found.");
            var members = DeviceGroupMembershipQuery.DeviceIds(db, [group]);
            devices = devices.Where(x => members.Contains(x.Id));
        }
        else if (deviceIds is not null) devices = devices.Where(x => deviceIds.Contains(x.Id));
        else devices = devices.Where(x => x.Id == deviceId);
        var targets = await devices.Include(x => x.Nics).ToListAsync(ct);
        if (targets.Count == 0) return new(0, 0);
        // Match MACs to their own adapter's subnet, never to another interface on the device.
        var entries = targets.SelectMany(d => d.Nics.Where(n => WakeNetwork.Mac(n.Mac) != null)
            .SelectMany(n => WakeNetwork.Addresses(n.IpNetworks).Select(a =>
                (d.Id, Mac: WakeNetwork.Mac(n.Mac)!, Valid: WakeNetwork.TryParse(a, out var subnet, out _, out _), Subnet: subnet))))
            .Where(x => x.Valid).Distinct().ToList();
        var cutoff = clock.GetUtcNow().UtcDateTime - AgentConnectionService.PresenceLifetime;
        var candidates = await db.SocketConnections.AsNoTracking().Where(x => x.ServerInstanceId == server.Id && x.LastSeenAtUtc > cutoff)
            .OrderByDescending(x => x.LastSeenAtUtc).Select(x => new { x.DeviceId, x.Device!.Guid, Networks = x.Device.Nics.Select(n => n.IpNetworks).ToList() }).ToListAsync(ct);
        var audit = new AuditLog { AppUserId = userId, UserName = user.Identity?.Name ?? userId.ToString(), AuditType = AuditType.Update,
            ObjectType = "Wake", ObjectId = deviceId ?? groupId ?? 0, ObjectName = groupId.HasValue ? "Group wake-up" : targets[0].Name,
            LoggedAtUtc = clock.GetUtcNow().UtcDateTime, ObjectJson = JsonSerializer.Serialize(new { deviceId, groupId, DeviceIds = targets.Select(x => x.Id).ToArray(), Count = targets.Count, Status = "Requested" }) };
        db.AuditLogs.Add(audit);
        await db.SaveChangesAsync(ct);
        var sentDevices = new HashSet<int>();
        foreach (var subnet in entries.GroupBy(x => x.Subnet))
        {
            var relays = candidates.Where(c => c.Networks.SelectMany(WakeNetwork.Addresses)
                .Any(a => WakeNetwork.TryParse(a, out var network, out _, out _) && network == subnet.Key)).ToList();
            foreach (var batch in subnet.Select(x => x.Mac).Distinct().Chunk(128))
            {
                var remaining = batch.ToHashSet();
                foreach (var relay in relays)
                {
                    ct.ThrowIfCancellationRequested();
                    var connection = await connections.FindActiveAsync(relay.DeviceId, ct);
                    if (connection is null || connection.ServerInstanceId != server.Id) continue;
                    var request = new AgentWakeRequest(Guid.NewGuid(), relay.Guid, clock.GetUtcNow().AddSeconds(30), subnet.Key, remaining.ToArray());
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(15));
                    try
                    {
                        var reply = await hub.Clients.Client(connection.ConnectionId).InvokeAsync<AgentWakeReply>(AgentHubProtocol.Wake, request, timeout.Token);
                        if (reply is null || reply.RequestId != request.RequestId || reply.SentMacs is null || reply.SentMacs.Length > batch.Length
                            || reply.SentMacs.Any(x => !remaining.Contains(x))) continue;
                        foreach (var mac in reply.SentMacs)
                        {
                            remaining.Remove(mac);
                            foreach (var target in subnet.Where(x => x.Mac == mac)) sentDevices.Add(target.Id);
                        }
                        if (remaining.Count == 0) break;
                    }
                    catch (Exception ex) when (ex is HubException or IOException || ex is OperationCanceledException && !ct.IsCancellationRequested) { }
                }
            }
        }
        var result = new WakeResult(targets.Count, sentDevices.Count) { SentDeviceIds = sentDevices.ToArray() };
        audit.ObjectJson = JsonSerializer.Serialize(new { deviceId, groupId, DeviceIds = targets.Select(x => x.Id).ToArray(), result.Requested, result.Sent });
        await db.SaveChangesAsync(ct);
        return result;
    }
}
