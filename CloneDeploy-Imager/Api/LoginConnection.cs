using System.Net;

namespace CloneDeploy_Imager.Api;

/// <summary>
/// Summarizes server reachability and the message displayed alongside the login form.
/// </summary>
internal sealed record LoginConnection(string Server, string Message, bool Reachable);

/// <summary>
/// Checks HTTPS server reachability before sign-in and describes connection failures for the login screen.
/// </summary>
internal sealed partial class ImagingClient
{
    public string ServerAddress => http.BaseAddress!.AbsoluteUri;

    public async Task<LoginConnection> CheckLoginConnectionAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            // A 401 is expected without credentials and confirms the session endpoint is reachable.
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/imaging/session");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var ready = response.StatusCode == HttpStatusCode.Unauthorized;
            return new(ServerAddress, ready ? "Server reachable over HTTPS" :
                $"HTTPS responded with HTTP {(int)response.StatusCode}; check server configuration or availability.", ready);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(ServerAddress, "Server connection timed out. Check network, routing, and server availability.", false);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return new(ServerAddress, "Main-server signature verification failed. Check the installation CA, device clock, and main/Imager versions.", false);
        }
        catch (HttpRequestException exception)
        {
            var message = exception.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError => "Server name could not be resolved. Check DNS and the server address.",
                HttpRequestError.ConnectionError => "Cannot connect to server. Check network, routing, firewall, and server availability.",
                HttpRequestError.SecureConnectionError => "HTTPS certificate or TLS validation failed. Check certificate trust and the device clock.",
                _ => "HTTPS request failed. Check the network and server configuration."
            };
            return new(ServerAddress, message, false);
        }
    }
}
