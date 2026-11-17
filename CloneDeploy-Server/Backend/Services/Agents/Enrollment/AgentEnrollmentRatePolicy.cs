using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace CloneDeploy_Server.Backend.Services;

public sealed class AgentEnrollmentRatePolicy(AgentEnrollmentRateSettings settings) : IRateLimiterPolicy<int>
{
    // All clients share the current limit. A new value uses a new limiter, not a partition per client IP.
    public RateLimitPartition<int> GetPartition(HttpContext httpContext) =>
        RateLimitPartition.GetFixedWindowLimiter(settings.RequestsPerMinute, limit => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
        });

    public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected => async (context, _) =>
    {
        var retry = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var remaining)
            ? Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds)) : 60;
        context.HttpContext.Response.Headers.RetryAfter = retry.ToString(CultureInfo.InvariantCulture);
        await Results.Problem(statusCode: 429, detail: "Too many enrollment requests. Retry later.").ExecuteAsync(context.HttpContext);
    };
}
