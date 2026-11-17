using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CloneDeploy_Agent;

// HTTP enrollment/check-ins and SignalR must apply exactly the same server trust rules.
/// <summary>
/// Checks that the agent is connecting to a trusted server.
/// </summary>
public static class AgentServerTrust
{
    private const int MaximumCaFileBytes = 64 * 1024;

    public static void ValidateConfiguration(AgentOptions options)
    {
        _ = GetRoot(options);
    }

    public static void Configure(HttpClientHandler handler, AgentOptions options)
    {
        handler.AllowAutoRedirect = false;
        var root = GetRoot(options);

        if (root is not null)
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) => MatchesRoot(
                certificate,
                errors,
                root);
    }

    public static void Configure(ClientWebSocketOptions socket, AgentOptions options)
    {
        var root = GetRoot(options);

        if (root is not null)
            socket.RemoteCertificateValidationCallback = (_, certificate, _, errors) => MatchesRoot(
                certificate,
                errors,
                root);
    }

    private static byte[]? GetRoot(AgentOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ServerCaCertificatePath))
            throw new InvalidOperationException("Agent:ServerCaCertificatePath is required. Install the trusted CloneDeploy CA supplied with the agent configuration.");

        if (!Path.IsPathFullyQualified(options.ServerCaCertificatePath)
            || options.ServerCaCertificatePath.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidOperationException("Agent:ServerCaCertificatePath must be an absolute local certificate path.");

        if (new FileInfo(options.ServerCaCertificatePath).Length > MaximumCaFileBytes)
            throw new InvalidOperationException("The server CA certificate file is too large.");

        using var root = X509CertificateLoader.LoadCertificateFromFile(options.ServerCaCertificatePath);

        if (root.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault()?.CertificateAuthority != true)
            throw new InvalidOperationException("The server trust file must contain a CA certificate.");

        return root.RawData;
    }

    private static bool MatchesRoot(X509Certificate? certificate, SslPolicyErrors errors, byte[] rootBytes)
    {
        if (certificate is null
            || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0)
            return false;
        using var server = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        using var root = X509CertificateLoader.LoadCertificate(rootBytes);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

        return chain.Build(server) && chain.ChainElements.Count == 2;
    }
}
