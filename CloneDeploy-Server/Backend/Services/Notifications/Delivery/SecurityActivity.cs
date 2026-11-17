using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.AspNetCore.Components.Authorization;

namespace CloneDeploy_Server.Backend.Services;

public sealed class SecurityActivity(ICloneDeployDbFactory factory, IHttpContextAccessor http,
    AuthenticationStateProvider authentication, TimeProvider clock, ILogger<SecurityActivity> logger)
{
    public const string Failure = "SignInFailed", Success = "SignInSucceeded", Lockout = "AccountLocked", Change = "ConfigurationChanged";

    public Task SignInAsync(string account, string channel, string outcome) => WriteAsync(outcome, channel, account, "");

    public async Task ChangeAsync(string category, string target)
    {
        try
        {
            var principal = (await authentication.GetAuthenticationStateAsync()).User;
            await WriteAsync(Change, category, principal.Identity?.Name ?? "Unknown administrator", target);
        }
        catch (Exception ex) { logger.LogWarning(ex, "Could not record security configuration activity"); }
    }

    private async Task WriteAsync(string kind, string category, string account, string target)
    {
        try
        {
            var ip = http.HttpContext?.Connection.RemoteIpAddress;
            if (ip?.IsIPv4MappedToIPv6 == true) ip = ip.MapToIPv4();
            await using var db = await factory.CreateContextAsync();
            db.Add(new SecurityActivityEvent
            {
                OccurredAtUtc = clock.GetUtcNow().UtcDateTime, Kind = Clean(kind, 32), Category = Clean(category, 100),
                Account = Clean(account, 256), SourceIp = ip?.ToString() ?? "", Target = Clean(target, 256)
            });
            await db.SaveChangesAsync();
        }
        // A telemetry outage must not change whether sign-in or an already committed change succeeded.
        catch (Exception ex) { logger.LogWarning(ex, "Could not record security activity"); }
    }

    private static string Clean(string value, int max) => new(value.Where(c => !char.IsControl(c)).Take(max).ToArray());
}
