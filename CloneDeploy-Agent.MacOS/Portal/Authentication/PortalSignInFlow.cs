using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using CloneDeploy_Agent.Contracts;
namespace CloneDeploy_Agent.MacOS.Portal;

internal static class PortalSignInFlow
{
    private const int MaximumCallbackHeaderBytes = 8192;

    private static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static async Task<MacPortalAuthReply> RunAsync(CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(5));
        var state = Encode(RandomNumberGenerator.GetBytes(32)); var verifier = Encode(RandomNumberGenerator.GetBytes(32));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(4);
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var start = await PortalClient.AuthenticateAsync(new("start", Start: new("", $"http://127.0.0.1:{port}/callback", state,
                Encode(SHA256.HashData(Encoding.UTF8.GetBytes(verifier))))), deadline.Token);
            if (start.Status != "Pending" || start.Start is null || !Uri.TryCreate(start.SignInUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https")
                throw new IOException(PortalText.Get("SignIn.CheckEnrollmentAndVersion"));
            var open = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false }; open.ArgumentList.Add(uri.AbsoluteUri);
            using (var process = Process.Start(open) ?? throw new IOException(PortalText.Get("SignIn.OpenBrowserFailed")))
            { await process.WaitForExitAsync(deadline.Token); if (process.ExitCode != 0) throw new IOException(PortalText.Get("SignIn.OpenBrowserFailed")); }
            while (true)
            {
                using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var stream = peer.GetStream();
                try
                {
                    var header = new List<byte>(); var next = new byte[1];
                    while (header.Count < MaximumCallbackHeaderBytes)
                    {
                        if (await stream.ReadAsync(next, timeout.Token) == 0) break;
                        header.Add(next[0]);
                        if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
                    }
                    var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n"); var first = lines[0].Split(' ');
                    if (header.Count >= MaximumCallbackHeaderBytes || first.Length != 3 || first[0] != "GET" || first[2] != "HTTP/1.1" || !first[1].StartsWith("/callback?", StringComparison.Ordinal) ||
                        lines.Count(x => x.Equals($"Host: 127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase)) != 1) continue;
                    var pairs = first[1][10..].Split('&').Select(x => x.Split('=', 2)).ToArray();
                    if (pairs.Length != 2 || pairs.Any(x => x.Length != 2)) continue;
                    var returnedState = pairs.FirstOrDefault(x => x[0] == "state")?[1]; var code = pairs.FirstOrDefault(x => x[0] == "code")?[1];
                    if (returnedState != state || code is not { Length: 43 } || !code.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) continue;
                    var result = await PortalClient.AuthenticateAsync(new("exchange", Exchange: new(start.Start.RequestId, "", code, verifier)), deadline.Token);
                    var success = result.Status == "SignedIn" && result.Session is not null;
                    var message = success ? PortalText.Get("SignIn.CompleteReturnToPortal") : PortalText.Get("SignIn.RestartFromPortal");
                    var body = Encoding.UTF8.GetBytes("<!doctype html><html><head><meta charset='utf-8'><title>" + WebUtility.HtmlEncode(PortalText.Get("SignIn.BrowserPageTitle")) + "</title></head><body><p>" + WebUtility.HtmlEncode(message) + "</p></body></html>");
                    var response = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nContent-Security-Policy: default-src 'none'; frame-ancestors 'none'\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                    // Browser disconnecting after redemption must not lose the issued session.
                    try { await stream.WriteAsync(response, timeout.Token); await stream.WriteAsync(body, timeout.Token); } catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
                    if (!success) throw new IOException(PortalText.Get("SignIn.ExpiredOrRejected"));
                    return result;
                }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
                catch (IOException) when (!deadline.IsCancellationRequested && !peer.Connected) { }
            }
        }
        finally { listener.Stop(); }
    }
}
