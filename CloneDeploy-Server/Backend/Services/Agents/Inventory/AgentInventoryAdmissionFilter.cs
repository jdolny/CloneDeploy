using System.Security.Claims;
using CloneDeploy_Server.Backend.Auth;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

// Resource filters run after authorization but before model binding reads the inventory payload.
/// <summary>
/// Limits how many device inventory reports can be submitted at once.
/// </summary>
public sealed class AgentInventoryAdmissionFilter(ICloneDeployDbFactory factory, TimeProvider clock) : IAsyncResourceFilter
{
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var http = context.HttpContext;
        var deviceId = int.Parse(http.User.FindFirstValue(AgentCertificateAuthentication.DeviceIdClaim)!);
        var lease = new AgentInventoryLease
        {
            Id = Guid.NewGuid(), DeviceId = deviceId,
            ExpiresAtUtc = clock.GetUtcNow().UtcDateTime.AddMinutes(2)
        };
        await using (var db = await factory.CreateContextAsync())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(http.RequestAborted);
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(903419)", http.RequestAborted);
            await db.Set<AgentInventoryLease>().Where(x => x.ExpiresAtUtc <= clock.GetUtcNow().UtcDateTime)
                .ExecuteDeleteAsync(http.RequestAborted);
            var limits = await DeviceTaskQueue.ReadLimitsAsync(db);
            if (await db.Set<AgentInventoryLease>().CountAsync(http.RequestAborted) >= limits.MaxConcurrentInventorySubmissions
                || await db.Set<AgentInventoryLease>().AnyAsync(x => x.DeviceId == deviceId, http.RequestAborted))
            {
                http.Response.Headers.RetryAfter = Random.Shared.Next(15, 46).ToString();
                context.Result = new ObjectResult(new ProblemDetails
                {
                    Status = 429, Detail = "Inventory capacity is busy. Retry shortly."
                }) { StatusCode = 429 };
                return;
            }
            db.Add(lease);
            await db.SaveChangesAsync(http.RequestAborted);
            await transaction.CommitAsync(http.RequestAborted);
        }

        var originalToken = http.RequestAborted;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(originalToken);
        var remaining = lease.ExpiresAtUtc - clock.GetUtcNow().UtcDateTime;
        deadline.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        http.RequestAborted = deadline.Token;
        try { await next(); }
        finally
        {
            http.RequestAborted = originalToken;
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var db = await factory.CreateContextAsync();
            await db.Set<AgentInventoryLease>().Where(x => x.Id == lease.Id).ExecuteDeleteAsync(cleanup.Token);
        }
    }
}
