using System.Security.Claims;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Auth;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed record DeviceRenameResult(bool Success, string Detail);

public sealed class DeviceRenameService(ICloneDeployDbFactory factory, AgentConnectionService connections,
    AgentServerInstance server, IHubContext<AgentHub> hub, IAuthorizationService authorization, ISecretProtector secrets)
{
    public async Task<List<ManagedCredentialDto>> GetRenameCredentialsAsync(ClaimsPrincipal user)
    {
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.DeviceEdit)).Succeeded)
            throw new UnauthorizedAccessException();
        await using var db = await factory.CreateContextAsync();
        return await db.ManagedCredentials.AsNoTracking().OrderBy(x => x.Name)
            .Select(x => new ManagedCredentialDto { Id = x.Id, Name = x.Name }).ToListAsync();
    }

    public async Task<DeviceRenameResult> RenameAsync(ClaimsPrincipal user, int deviceId, string name,
        bool renameConnectedWindows, int? credentialId = null, CancellationToken ct = default)
    {
        if (!await CanRenameAsync(user, renameConnectedWindows, credentialId, ct)
            || !int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            throw new UnauthorizedAccessException("Device permission is required.");
        name = name?.Trim() ?? "";
        if (ComputerNameValidation.Error(name) is { } error) return new(false, error);

        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(682947233, {deviceId})", ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended(lower({name}), 81237))", ct);
        var device = await db.Devices.SingleOrDefaultAsync(x => x.Id == deviceId && x.ArchivedAtUtc == null, ct);
        if (device is null) return new(false, "Device is unavailable.");
        if (await db.Devices.AnyAsync(x => x.Id != deviceId && x.Name.ToUpper() == name.ToUpper()
            && ((x.ArchivedAtUtc == null && x.ProvisionStatus != DeviceProvisionStatus.Archived)
                || x.DirectorySourceId != null || x.DirectoryObjectGuid != null), ct))
            return new(false, "That device name is already in use.");
        if (await db.Set<ImagingDeployment>().AnyAsync(x => x.DeviceId == deviceId && x.CancelledAtUtc == null && x.SetupCompletedAtUtc == null, ct))
            return new(false, "Finish or cancel the current imaging deployment before renaming this device.");

        var connection = renameConnectedWindows ? await connections.FindActiveAsync(deviceId, ct) : null;
        var detail = "Device record renamed. Windows was not changed; enable computer naming in the deployment profile to apply it during imaging.";
        var success = true;
        if (connection is not null)
        {
            if (connection.ServerInstanceId != server.Id) return new(false, "The agent is connected to another server instance. Rename it through that instance.");
            AgentPolicyCredential? credential = null;
            if (credentialId is { } id)
            {
                var stored = await db.ManagedCredentials.SingleOrDefaultAsync(x => x.Id == id, ct);
                if (stored is null) return new(false, "The selected managed credential no longer exists.");
                credential = new(stored.Username, secrets.Unprotect(stored.PasswordEncrypted));
            }
            var request = new AgentDeviceToolRequest(Guid.NewGuid(), device.Guid, DateTimeOffset.UtcNow.AddSeconds(90), AgentDeviceOperation.RenameComputer)
                { ComputerName = name, DomainCredential = credential };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(75));
            try
            {
                var reply = await hub.Clients.Client(connection.ConnectionId).InvokeAsync<AgentDeviceToolReply>(AgentHubProtocol.DeviceTools, request, timeout.Token);
                success = reply?.RequestId == request.RequestId && reply.Detail is { Length: <= 512 } && reply.Success;
                detail = reply?.RequestId == request.RequestId && reply.Detail is { Length: <= 512 } ? reply.Detail : "Agent returned an invalid rename response.";
            }
            catch (Exception ex) when (ex is OperationCanceledException or HubException or IOException)
            {
                success = false;
                detail = "Windows did not confirm the rename. Its name may have changed; check the device before retrying. The saved name was not changed.";
            }
        }
        var oldName = device.Name;
        if (success) device.Name = name;
        db.AuditLogs.Add(new AuditLog
        {
            AppUserId = userId, UserName = user.Identity?.Name ?? userId.ToString(), AuditType = AuditType.Update,
            ObjectType = nameof(Device), ObjectId = deviceId, ObjectName = oldName,
            ObjectJson = JsonSerializer.Serialize(new { Operation = "Rename", OldName = oldName, NewName = name, Live = connection is not null, Success = success, Detail = detail })
        });
        using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await db.SaveChangesAsync(finish.Token);
        await transaction.CommitAsync(finish.Token);
        return new(success, detail);
    }

    private async Task<bool> CanRenameAsync(ClaimsPrincipal user, bool renameConnectedWindows, int? credentialId, CancellationToken ct)
    {
        if (user.Identity?.AuthenticationType != ImagingAuthentication.SchemeName)
            return (await authorization.AuthorizeAsync(user, Constants.Permissions.DeviceEdit)).Succeeded;

        // ImagingBearer already validates token expiry, security stamp and imaging MFA.
        // Do not require browser-cookie claims or allow live agent commands through this path.
        if (!user.Identity.IsAuthenticated || user.FindFirstValue("imaging_kind") != "user"
            || renameConnectedWindows || credentialId is not null
            || !int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id)) return false;
        await using var db = await factory.CreateContextAsync();
        return await ImagingPermissions.HasAsync(db, id, Constants.Permissions.ImagingAccess, ct)
            && await ImagingPermissions.HasAsync(db, id, Constants.Permissions.DeviceEdit, ct);
    }
}
