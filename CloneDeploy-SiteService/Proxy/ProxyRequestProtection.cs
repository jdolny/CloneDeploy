using System.Diagnostics.Metrics;

namespace CloneDeploy.SiteService;

public sealed class ProxyRequestProtection(int requestsPerMinute, TimeProvider? timeProvider = null)
{
    private const int EnrollmentBodyLimitBytes = 24 * 1024;
    private const int LoginBodyLimitBytes = 8 * 1024;
    private const int RateWindowSeconds = 60;
    private static readonly TimeSpan BodyReadTimeout = TimeSpan.FromSeconds(30);

    private static readonly Meter Metrics = new("CloneDeploy.SiteService.Proxy");
    private static readonly Counter<long> Rejections = Metrics.CreateCounter<long>("proxy.rejected_requests");
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly Dictionary<string, (long Window, int Count)> windows = new();
    private int limitPerMinute = requestsPerMinute;
    private int loginLimitPerMinute = requestsPerMinute;

    public EnrollmentKeyGate EnrollmentKeys { get; } = new();

    public void Configure(int limit) => Configure(limit, limit);

    public void Configure(int limit, int loginLimit)
    {
        if (limit is < 0 or > CloneDeploy.SiteServices.SiteProxyProtocol.MaximumAuthenticationRequestsPerMinute || loginLimit is < 0 or > CloneDeploy.SiteServices.SiteProxyProtocol.MaximumAuthenticationRequestsPerMinute) throw new InvalidDataException("Invalid authentication rate limit.");
        lock (gate)
        {
            if (limitPerMinute == limit && loginLimitPerMinute == loginLimit) return;
            if (limitPerMinute != limit) windows.Remove("/api/agent/v1/enroll");
            if (loginLimitPerMinute != loginLimit) windows.Remove("/api/imaging/login");
            limitPerMinute = limit;
            loginLimitPerMinute = loginLimit;
        }
    }

    public static void Reject(HttpContext context, int status, string reason)
    {
        Rejections.Add(1, new KeyValuePair<string, object?>("reason", reason));
        context.Response.StatusCode = status;
    }

    public async Task<bool> AdmitAsync(HttpContext context)
    {
        var path = context.Request.Path.Value?.TrimEnd('/');
        // Encrypted login contents are private to main; share the site's login budget with session establishment.
        var imagingLogin = string.Equals(path, "/api/imaging/login", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, "/api/imaging/secure/handshake", StringComparison.OrdinalIgnoreCase);
        var limit = path?.ToLowerInvariant() switch
        {
            "/api/agent/v1/enroll" => EnrollmentBodyLimitBytes,
            "/api/imaging/login" => LoginBodyLimitBytes,
            "/api/imaging/secure/handshake" => 2048,
            _ => CloneDeploy.SiteServices.ListenerPorts.PortalRoute(context.Request.Path, context.Request.Method) && HttpMethods.IsPost(context.Request.Method) ? LoginBodyLimitBytes : 0
        };
        if (limit == 0) return true;
        lock (gate)
        {
            var requestLimit = imagingLogin ? loginLimitPerMinute : string.Equals(path, "/api/agent/v1/enroll", StringComparison.OrdinalIgnoreCase) ? limitPerMinute : 0;
            if (requestLimit > 0)
            {
                var seconds = clock.GetUtcNow().ToUnixTimeSeconds();
                var key = imagingLogin ? "/api/imaging/login" : path!.ToLowerInvariant();
                windows.TryGetValue(key, out var current);
                if (current.Window != seconds / RateWindowSeconds) current = (seconds / RateWindowSeconds, 0);
                if (current.Count >= requestLimit)
                {
                    context.Response.Headers.RetryAfter = (RateWindowSeconds - seconds % RateWindowSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    Reject(context, StatusCodes.Status429TooManyRequests, "authentication_rate");
                    return false;
                }
                windows[key] = (current.Window, current.Count + 1);
            }
        }
        if (context.Request.ContentLength > limit)
        { Reject(context, StatusCodes.Status413PayloadTooLarge, "authentication_body"); return false; }

        // Read only these small authentication bodies before forwarding. This also catches
        // chunked requests and prevents oversized bodies reaching the backend at all.
        var bytes = new byte[limit + 1];
        var length = 0;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(BodyReadTimeout);
        try
        {
            while (length < bytes.Length)
            {
                var read = await context.Request.Body.ReadAsync(bytes.AsMemory(length), deadline.Token);
                if (read == 0) break;
                length += read;
            }
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        { Reject(context, StatusCodes.Status408RequestTimeout, "authentication_body_timeout"); return false; }
        catch (BadHttpRequestException ex)
        { Reject(context, ex.StatusCode, "malformed_authentication_body"); return false; }
        if (length > limit)
        { Reject(context, StatusCodes.Status413PayloadTooLarge, "authentication_body"); return false; }
        if (string.Equals(path, "/api/agent/v1/enroll", StringComparison.OrdinalIgnoreCase) &&
            !EnrollmentKeys.Accepts(bytes.AsMemory(0, length)))
        { Reject(context, StatusCodes.Status401Unauthorized, "enrollment_key"); return false; }
        var body = new MemoryStream(bytes, 0, length, writable: false);
        context.Response.RegisterForDispose(body);
        context.Request.Body = body;
        context.Request.Headers.Remove("Transfer-Encoding");
        context.Request.ContentLength = length;
        return true;
    }
}
