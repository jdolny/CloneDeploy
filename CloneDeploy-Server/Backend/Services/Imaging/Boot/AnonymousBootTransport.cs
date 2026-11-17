using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.Extensions.Options;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>Explicitly permits plaintext transport for anonymous boot resources only.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AllowHttpBootAttribute : Attribute;

public static class AnonymousBootTransport
{
    // Run after routing: only explicitly marked controller actions bypass redirection.
    public static IApplicationBuilder UseBootAwareHttpsRedirection(this IApplicationBuilder app)
    {
        var services = app.ApplicationServices;
        var configuration = services.GetRequiredService<IConfiguration>();
        var options = services.GetRequiredService<IOptions<HttpsRedirectionOptions>>();
        var server = services.GetRequiredService<IServer>();
        // Inspect bound addresses at request time, after Kestrel has started. Fresh
        // installations intentionally serve HTTP until an HTTPS listener is enabled.
        bool HasHttpsDestination() => options.Value.HttpsPort is > 0 ||
            int.TryParse(configuration["HTTPS_PORT"] ?? configuration["ANCM_HTTPS_PORT"], out var port) && port > 0 ||
            server.Features.Get<IServerAddressesFeature>()?.Addresses.Any(address =>
                address.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) == true;
        return app.UseWhen(context => HasHttpsDestination() &&
            (!(HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)) ||
             context.GetEndpoint()?.Metadata.GetMetadata<AllowHttpBootAttribute>() is null),
            branch => branch.UseHttpsRedirection());
    }
}
