using System.Text.Json;
using CloneDeploy_Server.Backend.Auth;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;


/// <summary>
/// Retrieves the history of policies run on a device.
/// </summary>
public sealed class AgentPolicyHistoryService(ICloneDeployDbFactory factory, AuthenticationStateProvider authentication,
    IAuthorizationService authorization)
{
    public async Task<List<AgentPolicyHistoryRowDto>> ReadAsync(int deviceId)
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.DeviceView)).Succeeded) throw new UnauthorizedAccessException();
        await using var db = await factory.CreateContextAsync();
        var runs = await db.Set<AgentPolicyRun>().AsNoTracking().Where(x => x.DeviceId == deviceId)
            .OrderByDescending(x => x.StartedAtUtc).Take(100).ToListAsync();
        var history = new List<AgentPolicyHistoryRowDto>();
        foreach (var run in runs)
        {
            var snapshot = JsonSerializer.Deserialize<CompiledPolicySnapshotDto>(run.SnapshotJson)
                ?? throw new InvalidOperationException("The policy history snapshot is missing.");
            if (snapshot.Policy.Settings.GetProperty("SkipServerResult").GetBoolean()) continue;
            history.Add(new(run.Id, run.PolicyId, snapshot.Policy.Name, run.Hash,
                run.StartedAtUtc, run.FinishedAtUtc, run.Result, run.Detail));
        }
        return history;
    }
}
