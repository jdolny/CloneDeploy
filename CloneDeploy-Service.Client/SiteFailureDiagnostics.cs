using System.Net.Sockets;
using System.Security.Authentication;

namespace CloneDeploy.SiteService;

public static class SiteFailureDiagnostics
{
    public static string Describe(Exception error)
    {
        // Exception messages and response bodies can contain request data. Report only typed metadata.
        var details = new List<string>();
        for (Exception? current = error; current is not null && details.Count < 5; current = current.InnerException)
        {
            details.Add(current switch
            {
                HttpRequestException http => $"HTTP request error={http.HttpRequestError}; status={(http.StatusCode is { } status ? ((int)status).ToString() : "none")}",
                SocketException socket => $"TCP error={socket.SocketErrorCode}; native code={socket.NativeErrorCode}",
                AuthenticationException => "TLS authentication failed; check the server certificate hostname, CA, validity dates and TLS listener",
                OperationCanceledException => "Request timed out or was canceled",
                _ => $"{current.GetType().Name}; HRESULT=0x{current.HResult:X8}"
            });
        }
        return string.Join(" -> ", details);
    }
}
