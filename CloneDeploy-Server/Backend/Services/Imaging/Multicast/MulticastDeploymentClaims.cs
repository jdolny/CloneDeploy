using CloneDeploy.SiteServices;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed partial class ImagingDeploymentClaims
{
    public async Task StartMulticastAsync(string? token, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        var deployment = await RequireExecutionAsync(db, token, ct);
        if (deployment.MulticastSessionId is not { } id || deployment.ImagerStartedAtUtc == null)
            throw new InvalidOperationException("Only an admitted multicast participant can start its session.");
        var changed = await db.Set<MulticastSession>().Where(x => x.Id == id && x.State == MulticastState.Waiting &&
                x.SenderLease != null && x.SenderLeaseExpiresAtUtc > DateTime.UtcNow)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.StartRequested, true), ct);
        if (changed == 0) throw new InvalidOperationException("The multicast sender is not waiting for receivers.");
    }

    private static async Task<MulticastSession?> MulticastAsync(CloneDeployDbContext db, ImagingDeployment deployment, CancellationToken ct)
    {
        if (deployment.MulticastSessionId is not { } id) return null;
        var session = await db.Set<MulticastSession>().SingleAsync(x => x.Id == id, ct);
        if (session.State is MulticastState.Preparing or MulticastState.Failed or MulticastState.Cancelled ||
            session.State < MulticastState.Completed && session.SenderLeaseExpiresAtUtc < DateTime.UtcNow)
            throw new InvalidOperationException(session.Error ?? "The multicast session is unavailable.");
        return session;
    }
}
