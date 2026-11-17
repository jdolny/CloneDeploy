using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
namespace CloneDeploy_Agent.MacOS;

public sealed class MacIdentity : IDisposable
{
    private const int ClientKeySizeBits = 3072;

    private const string ClientAuthenticationOid = "1.3.6.1.5.5.7.3.2";
    private readonly MacStateStore store;
    private RSA key;
    private readonly object identityLock = new();
    public MacIdentityState State { get; private set; }
    public MacIdentity(MacStateStore store, MacConfiguration configuration)
    {
        this.store = store;
        var options = configuration.Options(store.DirectoryPath);
        State = store.Read<MacIdentityState>("identity.json") ?? CreateState(options.ServerUrl, options.DeviceName);
        if (State.SchemaVersion != 1 || State.InstallationId == Guid.Empty || State.ServerUrl != options.ServerUrl)
            throw new InvalidDataException("Agent identity is invalid or belongs to another server.");
        key = RSA.Create();
        try
        {
            var bytes = Convert.FromBase64String(State.PrivateKeyPkcs8);
            try { key.ImportPkcs8PrivateKey(bytes, out var read); if (read != bytes.Length || key.KeySize != ClientKeySizeBits) throw new InvalidDataException("Invalid agent private key."); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            // Confirm persisted CSR still belongs to the key; never silently replace a damaged identity.
            var csr = CertificateRequest.LoadSigningRequestPem(State.CsrPem, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.Default, RSASignaturePadding.Pkcs1);
            if (!csr.PublicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(key.ExportSubjectPublicKeyInfo()))
                throw new InvalidDataException("Stored signing request does not match the private key.");
            if (State.CertificatePem is null && (State.DeviceGuid is not null || State.CaCertificatePem is not null))
                throw new InvalidDataException("Incomplete agent certificate state.");
            store.Save("identity.json", State);
        }
        catch { key.Dispose(); throw; }
    }
    internal static MacIdentityState CreateState(string server, string name)
    {
        using var key = RSA.Create(ClientKeySizeBits);
        var request = new CertificateRequest("CN=CloneDeploy Agent", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var bytes = key.ExportPkcs8PrivateKey();
        try { return new() { ServerUrl = server, DeviceName = name, PrivateKeyPkcs8 = Convert.ToBase64String(bytes), CsrPem = request.CreateSigningRequestPem() }; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public static void RequestReset(MacStateStore store, MacConfiguration configuration)
    {
        var previous = store.Read<MacIdentityState>("identity.json") ?? throw new InvalidOperationException("No identity to reset.");
        var options = configuration.Options(store.DirectoryPath);
        if (previous.ServerUrl != options.ServerUrl || previous.SchemaVersion != 1) throw new InvalidDataException("Identity and configuration do not match.");
        store.Save("identity.json", CreateState(options.ServerUrl, options.DeviceName) with { InstallationId = previous.InstallationId, RequestReset = true });
    }
    public void AcceptCertificate(Guid deviceGuid, string pem, string caPem)
    {
        lock (identityLock)
        {
            using var certificate = ValidateCertificate(deviceGuid, pem, caPem);
            var state = State with { DeviceGuid = deviceGuid, CertificatePem = pem, CaCertificatePem = caPem, RequestReset = false };
            store.Save("identity.json", state);
            State = state;
        }
    }
    public string? PrepareRenewal()
    {
        lock (identityLock)
        {
            if (!AgentCertificateLifecycle.NeedsRenewal(State.CertificatePem)) return null;
            if (State.PendingPrivateKeyPkcs8 is null)
            {
                var pending = CreateState(State.ServerUrl, State.DeviceName);
                Save(State with { PendingPrivateKeyPkcs8 = pending.PrivateKeyPkcs8, PendingCsrPem = pending.CsrPem });
            }
            if (State.PendingCsrPem is null) throw new InvalidDataException("Incomplete pending renewal identity.");
            return State.PendingCsrPem;
        }
    }

    public void AcceptRenewal(CloneDeploy_Agent.Contracts.AgentCertificateRenewal renewal)
    {
        lock (identityLock)
        {
            AgentCertificateLifecycle.ValidateReplacement(State.CertificatePem!, State.CaCertificatePem!, renewal);
            if (State.PendingPrivateKeyPkcs8 is null || State.PendingCsrPem is null)
                throw new InvalidDataException("No pending renewal key.");
            var pending = RSA.Create();
            try
            {
                var bytes = Convert.FromBase64String(State.PendingPrivateKeyPkcs8);
                try { pending.ImportPkcs8PrivateKey(bytes, out _); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
                using var validated = ValidateCertificate(State.DeviceGuid!.Value, renewal.CertificatePem, renewal.CaCertificatePem, pending);
                Save(State with { PreviousPrivateKeyPkcs8 = State.PrivateKeyPkcs8, PrivateKeyPkcs8 = State.PendingPrivateKeyPkcs8,
                    CsrPem = State.PendingCsrPem, CertificatePem = renewal.CertificatePem,
                    PendingPrivateKeyPkcs8 = null, PendingCsrPem = null });
            }
            catch { pending.Dispose(); throw; }
            var old = key;
            key = pending;
            old.Dispose();
        }
    }

    public void CompleteRenewal()
    {
        lock (identityLock)
        {
            if (State.PreviousPrivateKeyPkcs8 is not null)
                Save(State with { PreviousPrivateKeyPkcs8 = null });
        }
    }

    private void Save(MacIdentityState state)
    {
        store.Save("identity.json", state);
        State = state;
    }

    public X509Certificate2? GetClientCertificate()
    {
        lock (identityLock)
        {
            if (State.CertificatePem is null) return null;
            using var certificate = ValidateCertificate(State.DeviceGuid ?? Guid.Empty, State.CertificatePem, State.CaCertificatePem ?? "");
            using var combined = certificate.CopyWithPrivateKey(key);
            // Use the platform's normal private-key import. macOS TLS requires a keychain-backed identity;
            // EphemeralKeySet is unsupported there, and Schannel also rejects our imported in-memory RSA key.
            var pfx = combined.Export(X509ContentType.Pfx);
            try { return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.DefaultKeySet); }
            finally { CryptographicOperations.ZeroMemory(pfx); }
        }
    }
    private X509Certificate2 ValidateCertificate(Guid deviceGuid, string pem, string caPem, RSA? expectedKey = null)
    {
        var cert = X509Certificate2.CreateFromPem(pem);

        try
        {
            using var ca = X509Certificate2.CreateFromPem(caPem);
            using var publicKey = cert.GetRSAPublicKey();
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(ca);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid(ClientAuthenticationOid));

            if (deviceGuid == Guid.Empty
                || cert.GetNameInfo(X509NameType.SimpleName, false) != deviceGuid.ToString())
            {
                throw new InvalidDataException("The device certificate does not match the assigned device ID.");
            }

            if (publicKey is null
                || !publicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual((expectedKey ?? key).ExportSubjectPublicKeyInfo()))
            {
                throw new InvalidDataException("The device certificate does not match the agent's private key.");
            }

            var constraints = cert.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault();

            if (constraints?.CertificateAuthority != false)
            {
                throw new InvalidDataException("The device certificate must explicitly be a non-CA certificate.");
            }

            var keyUsage = cert.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault();

            if (keyUsage?.KeyUsages != X509KeyUsageFlags.DigitalSignature)
            {
                throw new InvalidDataException("The device certificate must allow only digital signatures.");
            }

            var allowsClientAuthentication = cert.Extensions.OfType<X509EnhancedKeyUsageExtension>()
                .Any(extension => extension.EnhancedKeyUsages.Cast<Oid>()
                    .Any(usage => usage.Value == ClientAuthenticationOid));

            if (!allowsClientAuthentication)
            {
                throw new InvalidDataException("The device certificate must allow client authentication.");
            }

            if (!chain.Build(cert) || chain.ChainElements.Count != 2)
            {
                throw new InvalidDataException("The device certificate must be valid and signed directly by the supplied CA.");
            }

            return cert;
        }
        catch
        {
            cert.Dispose();
            throw;
        }
    }

    public void Dispose() => key.Dispose();
}
