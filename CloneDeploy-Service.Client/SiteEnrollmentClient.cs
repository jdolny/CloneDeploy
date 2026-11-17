using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using CloneDeploy.SiteServices;

namespace CloneDeploy.SiteService;

public static class SiteEnrollmentClient
{
    private const int MaximumSetupBytes = 32 * 1024;
    private const int IdentityKeyBits = 3072;
    private const int LocalSecretBytes = 32;
    private const int EnrollmentTokenHexCharacters = 64;
    private const int MaximumReportedAddresses = 32;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    public static void Import(string path, SiteLocalStore store)
    {
        if (File.Exists(store.StatePath)) throw new InvalidOperationException("This machine already has enrollment state. Use its existing identity; a replacement requires a new registration and a new state directory.");
        if (new FileInfo(path).Length > MaximumSetupBytes) throw new InvalidDataException("The setup file is too large.");
        var package = JsonSerializer.Deserialize<SiteEnrollmentPackage>(File.ReadAllText(path)) ?? throw new InvalidDataException("Invalid setup file.");
        ValidatePackage(package);
        if (store.ExpectedWindowsWorker is { } expected && package.IsWindowsWorker != expected)
            throw new InvalidDataException("This setup JSON belongs to a different service type.");
        using var identity = RSA.Create(IdentityKeyBits); using var https = RSA.Create(IdentityKeyBits);
        var identityCsr = new CertificateRequest("CN=SiteService identity", identity, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequestPem();
        var httpsCsr = new CertificateRequest("CN=SiteService HTTPS", https, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequestPem();
        store.Save(new(package, identity.ExportPkcs8PrivateKeyPem(), https.ExportPkcs8PrivateKeyPem(), identityCsr, httpsCsr,
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(LocalSecretBytes)), null), create: true);
        Console.WriteLine("Setup file imported. Start " + (package.IsWindowsWorker ? "CloneDeploy-WindowsWorker" : "CloneDeploy-SiteService") + " and approve the matching request in Admin Settings > Site Services.");
        Console.WriteLine("Identity key fingerprint: " + SiteEnrollmentCrypto.KeyFingerprint(SiteEnrollmentCrypto.ReadCsr(identityCsr)));
    }

    public static void ValidatePackage(SiteEnrollmentPackage package)
    {
        if (package.FormatVersion != 1 || package.RegistrationId == Guid.Empty || package.ExpiresAtUtc <= DateTime.UtcNow ||
            package.Token is null || package.Token.Length != EnrollmentTokenHexCharacters || !package.Token.All(Uri.IsHexDigit) ||
            package.CaCertificatePem is null || package.CaCertificatePem.Contains("PRIVATE KEY", StringComparison.Ordinal) ||
            !Uri.TryCreate(package.BackendUrl, UriKind.Absolute, out var backend) || backend.Scheme != "https" ||
            backend.UserInfo.Length != 0 || backend.AbsolutePath != "/" || backend.Query.Length != 0 || backend.Fragment.Length != 0)
            throw new InvalidDataException("The setup file is invalid or expired. Download a new file from the main server.");
        BackendConnection.Parse(package.BackendAddress);
        using var ca = X509Certificate2.CreateFromPem(package.CaCertificatePem);
        if (ca.HasPrivateKey || ca.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault()?.CertificateAuthority != true ||
            ca.NotAfter.ToUniversalTime() <= DateTime.UtcNow || ca.NotBefore.ToUniversalTime() > DateTime.UtcNow)
            throw new InvalidDataException("The setup file does not contain a valid public CA.");
    }

    public static HttpClient CreateClient(SiteLocalState state, bool authenticate)
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false };
        BackendConnection.Configure(handler, state.Package.BackendAddress);
        // The service listener must chain to the CA delivered in the enrollment package.
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, presentedChain, errors) =>
        {
            if (certificate is null || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0) return false;
            using var ca = X509Certificate2.CreateFromPem(state.Package.CaCertificatePem);
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust; chain.ChainPolicy.CustomTrustStore.Add(ca);
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
            chain.ChainPolicy.DisableCertificateDownloads = true; chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            if (presentedChain is not null) foreach (var element in presentedChain.ChainElements) chain.ChainPolicy.ExtraStore.Add(element.Certificate);
            using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            return chain.Build(leaf);
        };
        X509Certificate2? identity = null;
        if (authenticate)
        {
            identity = WithKey(state.Configuration!.IdentityCertificatePem, state.IdentityKey);
            handler.SslOptions.ClientCertificates = new X509CertificateCollection { identity };
        }
        return new OwnedClient(handler, identity) { BaseAddress = new Uri(state.Package.BackendUrl), Timeout = RequestTimeout, MaxResponseContentBufferSize = MaximumSetupBytes };
    }

    public static SiteEnrollmentRequest Request(SiteLocalState state)
    {
        var addresses = NetworkInterface.GetAllNetworkInterfaces().Where(x => x.OperationalStatus == OperationalStatus.Up)
            .SelectMany(x => x.GetIPProperties().UnicastAddresses).Select(x => x.Address.ToString()).Distinct().Take(MaximumReportedAddresses).ToArray();
        var request = new SiteEnrollmentRequest(state.Package.RegistrationId, state.Package.Token, state.IdentityCsr, state.HttpsCsr,
            Environment.MachineName, Version, addresses, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "")
            { IsWindowsWorker = state.Package.IsWindowsWorker, HostOs = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : "Other" };
        using var key = RSA.Create(); key.ImportFromPem(state.IdentityKey);
        return request with { Signature = Convert.ToBase64String(key.SignData(request.SigningBytes(), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) };
    }

    public static SiteLocalState Accept(SiteLocalState state, SiteConfiguration configuration, SiteLocalStore store)
    {
        if (configuration.RegistrationId != state.Package.RegistrationId || configuration.BackendUrl != state.Package.BackendUrl ||
            configuration.IsWindowsWorker != state.Package.IsWindowsWorker ||
            configuration.Revision < (state.Configuration?.Revision ?? 0)) throw new InvalidDataException("Unexpected service configuration.");
        using var ca = X509Certificate2.CreateFromPem(state.Package.CaCertificatePem);
        foreach (var (pem, key, usage) in new[] { (configuration.IdentityCertificatePem, state.IdentityKey, "1.3.6.1.5.5.7.3.2"),
                     (configuration.HttpsCertificatePem, state.HttpsKey, "1.3.6.1.5.5.7.3.1") })
        {
            using var certificate = WithKey(pem, key);
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust; chain.ChainPolicy.CustomTrustStore.Add(ca);
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid(usage)); chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            if (!chain.Build(certificate) || certificate.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault()?.CertificateAuthority != false)
                throw new InvalidDataException("The server returned an invalid service certificate.");
        }
        var updated = state with { Package = state.Package with { Token = "" }, Configuration = configuration };
        store.Save(updated); return updated;
    }

    public static X509Certificate2 WithKey(string pem, string key)
    {
        using var certificate = X509Certificate2.CreateFromPem(pem, key);
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null,
            OperatingSystem.IsWindows() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet);
    }
    public static string Version => typeof(SiteEnrollmentClient).Assembly.GetName().Version?.ToString() ?? "unknown";
    private sealed class OwnedClient(SocketsHttpHandler handler, X509Certificate2? identity) : HttpClient(handler)
    { protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) identity?.Dispose(); } }
}
