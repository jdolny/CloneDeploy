using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using System.Threading.RateLimiting;

namespace CloneDeploy_Server.Backend.Auth;

public static class LocalAuthEndpoints
{
    public static void AddBrowserLoginProtection(this IServiceCollection services) => services.AddRateLimiter(options =>
    {
        options.AddPolicy("BrowserLogin", context => RateLimitPartition.GetFixedWindowLimiter(
            (context.Connection.RemoteIpAddress?.IsIPv4MappedToIPv6 == true
                ? context.Connection.RemoteIpAddress.MapToIPv4() : context.Connection.RemoteIpAddress)?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    });

    public static IEndpointRouteBuilder MapLocalAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/auth/login", async (LocalLoginService login, [FromForm] LoginRequestDto request) =>
            {
                var returnUrl = NormalizeReturnUrl(request.ReturnUrl);
                var result = await login.LoginAsync(request);
                if (result.Failed)
                    return Results.Redirect($"/login?returnUrl={Uri.EscapeDataString(returnUrl)}&error=unavailable");

                if (result.Succeeded)
                {
                    if (result.RequiresEnrollment)
                        return Results.Redirect($"/account/mfa?returnUrl={Uri.EscapeDataString(returnUrl)}");
                    return Results.Redirect(returnUrl);
                }

                if (result.RequiresTwoFactor)
                    return Results.Redirect($"/login/mfa?returnUrl={Uri.EscapeDataString(returnUrl)}");

                if (result.IsLockedOut)
                    return Results.Redirect($"/login?returnUrl={Uri.EscapeDataString(returnUrl)}&error=locked");

                return Results.Redirect($"/login?returnUrl={Uri.EscapeDataString(returnUrl)}&error=invalid");
            })
            .AllowAnonymous()
            .RequireRateLimiting("BrowserLogin");

        app.MapPost("/auth/login/mfa", async (
                [FromForm] MfaLoginRequestDto request,
                LocalLoginService login) =>
            {
                var returnUrl = NormalizeReturnUrl(request.ReturnUrl);
                var result = await login.MfaAsync(request);

                if (result.Failed)
                    return Results.Redirect($"/login?returnUrl={Uri.EscapeDataString(returnUrl)}&error=unavailable");

                if (result.Succeeded)
                    return Results.Redirect(returnUrl);

                if (result.IsLockedOut)
                    return Results.Redirect($"/login?returnUrl={Uri.EscapeDataString(returnUrl)}&error=locked");

                return Results.Redirect($"/login/mfa?returnUrl={Uri.EscapeDataString(returnUrl)}&error=invalid");
            })
            .AllowAnonymous()
            .RequireRateLimiting("BrowserLogin");

        app.MapPost("/auth/logout", async (SignInManager<AppUser> signInManager) =>
            {
                await signInManager.SignOutAsync();
                return Results.Redirect("/login");
            })
            // MFA enrollment/reset invalidates the current security stamp. Logout must
            // still clear those cookies and take the browser back to sign-in.
            .AllowAnonymous()
            .DisableAntiforgery();

        return app;
    }

    private static string NormalizeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
            return "/";

        if (!returnUrl.StartsWith('/') || returnUrl.StartsWith("//"))
            return "/";

        return returnUrl;
    }
}
