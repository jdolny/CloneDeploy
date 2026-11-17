using System.Text.Json;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed class AgentActivityService(ICloneDeployDbFactory factory, TimeProvider clock)
{
    public async Task<bool> ReportAsync(int deviceId, Guid certificateId, string connectionId,
        AgentActivity report, CancellationToken ct)
    {
        if (report.Sequence < 0 || !System.Enum.IsDefined(report.Kind) ||
            ((report.Kind is AgentActivityKind.RunningTask or AgentActivityKind.RunningPolicy) != report.RunId.HasValue) ||
            report.RunId == Guid.Empty) return false;
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Lock the current socket row, so a replaced socket cannot overwrite a reconnect snapshot.
        var sockets = await db.SocketConnections.FromSqlInterpolated($"""
            SELECT * FROM "SocketConnections" WHERE "DeviceId" = {deviceId} FOR UPDATE
            """).ToListAsync(ct);
        var socket = sockets.SingleOrDefault();
        if (socket?.ConnectionId != connectionId || socket.AgentCertificateId != certificateId) return false;
        var saved = await db.Set<DeviceActivityReport>().FindAsync([deviceId], ct);
        if (saved?.ConnectionId == connectionId && saved.Sequence >= report.Sequence) return true;
        string? name = null;
        if (report.Kind == AgentActivityKind.RunningTask)
        {
            name = await db.DeviceTasks.Where(x => x.Id == report.RunId && x.DeviceId == deviceId)
                .Select(x => x.Deployment.ActionName).SingleOrDefaultAsync(ct);
            if (name is null) return false;
        }
        if (report.Kind == AgentActivityKind.RunningPolicy)
        {
            var snapshot = await db.Set<AgentPolicyRun>().Where(x => x.Id == report.RunId && x.DeviceId == deviceId)
                .Select(x => x.SnapshotJson).SingleOrDefaultAsync(ct);
            if (snapshot is null) return false;
            using var document = JsonDocument.Parse(snapshot);
            name = document.RootElement.GetProperty("Policy").GetProperty("Name").GetString();
        }
        if (saved is null)
        {
            saved = new DeviceActivityReport { DeviceId = deviceId, ConnectionId = connectionId };
            db.Add(saved);
        }
        saved.ConnectionId = connectionId;
        saved.Sequence = report.Sequence;
        saved.Kind = report.Kind;
        saved.RunId = report.RunId;
        saved.Name = name?[..Math.Min(name.Length, 512)];
        saved.ChangedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }
}
