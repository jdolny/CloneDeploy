namespace CloneDeploy_Server.Backend.Services;

public sealed class ServerFeatureApiMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ServerFeatureSettings settings)
    {
        var agent = context.Request.Path.StartsWithSegments("/api/agent");
        var imaging = context.Request.Path.StartsWithSegments("/api/imaging");
        if (agent || imaging)
        {
            var features = await settings.ReadAsync(context.RequestAborted);
            if ((agent && !features.Management) || (imaging && !features.Imaging))
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.Headers.CacheControl = "no-store";
                await context.Response.WriteAsJsonAsync(new { error = agent ? "Management is disabled on this server." : "Imaging is disabled on this server." }, context.RequestAborted);
                return;
            }
        }
        await next(context);
    }
}
