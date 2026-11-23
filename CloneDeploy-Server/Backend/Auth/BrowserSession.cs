using Microsoft.AspNetCore.Authentication.Cookies;

namespace CloneDeploy_Server.Backend.Auth;

public static class BrowserSession
{
    public const string Path = "/api/ui/browser-session";

    // Ignore cookies issued by the former "Remember this machine" option.
    public static void DisableRememberedMfa(CookieAuthenticationOptions options)
    {
        options.Events.OnValidatePrincipal = context =>
        {
            context.RejectPrincipal();
            context.ShouldRenew = false;
            context.Response.Cookies.Delete(options.Cookie.Name!, options.Cookie.Build(context.HttpContext));
            return Task.CompletedTask;
        };
    }

    // Passive checks must not keep an unattended browser signed in indefinitely.
    public static void ConfigureCookie(CookieAuthenticationOptions options)
    {
        var sliding = options.Events.OnCheckSlidingExpiration;
        options.Events.OnCheckSlidingExpiration = async context =>
        {
            await sliding(context);
            if (context.Request.Path == Path && context.Request.Headers["X-CloneDeploy-User-Activity"] != "1")
                context.ShouldRenew = false;
        };
        var validate = options.Events.OnValidatePrincipal;
        options.Events.OnValidatePrincipal = async context =>
        {
            await validate(context);
            if (context.Request.Path == Path && context.Request.Headers["X-CloneDeploy-User-Activity"] != "1")
                context.ShouldRenew = false;
        };
    }
}
