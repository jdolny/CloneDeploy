using System.Security.Claims;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;

namespace CloneDeploy_Server.Backend.Services;

public interface IScheduledPowerActions
{
    Task<bool> CanRunAsync(int userId);
    Task<IReadOnlyDictionary<int, string?>> WakeAsync(int userId, int[] deviceIds, CancellationToken ct);
    Task<string?> RunAsync(int userId, DevicePowerAction action, int deviceId, CancellationToken ct);
}

// Resolve current database-backed claims: removing group power access also stops the user's assignments.
public sealed class ScheduledPowerActions(UserManager<AppUser> users, IUserClaimsPrincipalFactory<AppUser> principals,
    IAuthorizationService authorization, AgentWakeService wake, AgentDeviceToolService tools) : IScheduledPowerActions
{
    private readonly Dictionary<int, ClaimsPrincipal> _users = [];
    public async Task<IReadOnlyDictionary<int, string?>> WakeAsync(int userId, int[] deviceIds, CancellationToken ct)
    {
        if (!_users.TryGetValue(userId, out var principal)) throw new UnauthorizedAccessException();
        var result = await wake.DevicesAsync(principal, deviceIds, ct);
        return deviceIds.ToDictionary(id => id, id => result.SentDeviceIds.Contains(id) ? null : "No usable network inventory or available relay.");
    }
    public async Task<bool> CanRunAsync(int userId)
    {
        _users.Remove(userId);
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null || await users.IsLockedOutAsync(user)) return false;
        var principal = await principals.CreateAsync(user);
        if (!(await authorization.AuthorizeAsync(principal, Constants.Permissions.DeviceGroupPowerManagement)).Succeeded) return false;
        _users[userId] = principal;
        return true;
    }

    public async Task<string?> RunAsync(int userId, DevicePowerAction action, int deviceId, CancellationToken ct)
    {
        if (!_users.TryGetValue(userId, out var principal)) throw new UnauthorizedAccessException();
        if (action == DevicePowerAction.Wake)
        {
            var result = await wake.DevicesAsync(principal, [deviceId], ct);
            return result.Requested > 0 && result.Sent == result.Requested ? null : result.Detail;
        }
        if (action != DevicePowerAction.Shutdown) throw new InvalidOperationException("Invalid power action.");
        var reply = await tools.ShutdownGroupMemberAsync(principal, deviceId, ct);
        return reply.Success ? null : reply.Detail;
    }
}
