using System.Security.Claims;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed partial class AgentEnrollmentService
{
    private static void SetRequestHardware(AgentProvisioningRequest request, AgentEnrollmentRequestDto input)
    {
        request.HardwareUuid = Guid.TryParse(input.HardwareUuid, out var uuid) && uuid != Guid.Empty &&
            uuid != Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff") ? uuid.ToString("D") : null;
        request.MacAddresses = (input.MacAddresses ?? []).Select(NativePxeReservations.Normalize)
            .Where(x => x is not null).Select(x => x!).Distinct().ToArray();
    }

    public async Task DismissAsync(ClaimsPrincipal user, Guid id)
    {
        var userId = await RequireApprovalPermission(user);
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        await Lock(db, default);
        var request = await db.AgentProvisioningRequests.SingleOrDefaultAsync(x => x.Id == id)
            ?? throw new AgentEnrollmentException(404, "Enrollment request not found.");
        if (request.State is not (AgentProvisioningState.PendingApproval or AgentProvisioningState.PendingPreProvision or AgentProvisioningState.PendingReset))
            throw new AgentEnrollmentException(409, "Only unanswered enrollment requests can be dismissed.");
        db.AgentProvisioningRequests.Remove(request);
        db.AuditLogs.Add(Audit(userId, user.Identity?.Name, "AgentEnrollmentDismissed", request.DeviceName,
            new { request.Id, request.InstallationId }, AuditType.Update));
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    public async Task<int> ExpirePendingAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await Lock(db, ct);
        var settings = await ReadSettings(db, ct);
        var cutoff = clock.GetUtcNow().UtcDateTime.AddDays(-Math.Clamp(settings.Options.PendingRetentionDays, 1, 3650));
        var count = await db.AgentProvisioningRequests.Where(x =>
            (x.State == AgentProvisioningState.PendingApproval || x.State == AgentProvisioningState.PendingPreProvision || x.State == AgentProvisioningState.PendingReset)
            && x.LastSeenAtUtc < cutoff).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
        return count;
    }

    public async Task DeleteRequestAsync(ClaimsPrincipal user, Guid id)
    {
        var userId = await RequireApprovalPermission(user);
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        await Lock(db, default);
        var request = await db.AgentProvisioningRequests.SingleOrDefaultAsync(x => x.Id == id)
            ?? throw new AgentEnrollmentException(404, "Enrollment request not found.");
        if (request.State == AgentProvisioningState.Provisioned)
            throw new AgentEnrollmentException(409, "Completed enrollment records cannot be deleted from the request queue.");
        db.AgentProvisioningRequests.Remove(request);
        db.AuditLogs.Add(Audit(userId, user.Identity?.Name, "AgentEnrollmentRequestDeleted", request.DeviceName,
            new { request.Id, request.InstallationId, request.State, request.TargetDeviceId }, AuditType.Update));
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }
}

public sealed class AgentEnrollmentRequestCleanup(IServiceScopeFactory scopes, ILogger<AgentEnrollmentRequestCleanup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var count = await scope.ServiceProvider.GetRequiredService<AgentEnrollmentService>().ExpirePendingAsync(stoppingToken);
                if (count > 0) logger.LogInformation("Expired {Count} inactive pending enrollment requests.", count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Pending enrollment request cleanup failed."); }
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }
}
