using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CloneDeploy_Agent;

/// <summary>
/// Manages the device identity and credentials used to prove itself to the server.
/// </summary>
public sealed class AgentIdentity : IDisposable
{
    private const int ClientKeySizeBits = 3072;

    private const string ClientAuthenticationOid = "1.3.6.1.5.5.7.3.2";
    private const string KeyNamePrefix = "CloneDeploy.Agent.";

    private readonly AgentStore store;
    private readonly bool ownsStore;
    private RSACng key;
    private readonly object identityLock = new();

    public IdentityState State { get; private set; }

    public AgentIdentity(AgentOptions options) : this(options, new AgentStore(options.StateDirectory), true)
    {
    }

    public AgentIdentity(AgentOptions options, AgentStore store) : this(options, store, false)
    {
    }

    private AgentIdentity(AgentOptions options, AgentStore store, bool ownsStore)
    {
        this.store = store;
        this.ownsStore = ownsStore;

        try
        {
            var server = options.GetServerUri().AbsoluteUri;

            if (string.IsNullOrWhiteSpace(options.DeviceName)
                || options.DeviceName.Length > AgentOptions.MaximumDeviceNameLength
                || options.DeviceName.Any(char.IsControl))
            {
                throw new InvalidOperationException("Agent:DeviceName must contain 1–128 printable characters.");
            }

            options.ProvisioningKey = string.IsNullOrEmpty(options.ProvisioningKey)
                ? store.GetProvisioningKey() ?? ""
                : options.ProvisioningKey;
            store.SaveConfiguration(options);
            State = LoadOrCreateState(server, options.DeviceName);

            if (!CngKey.Exists(State.KeyName) && (State.CsrPem is not null || State.CertificatePem is not null))
            {
                // A lost local key needs a new key and explicit server approval.
                store.ResetIdentity(options, fullReset: false);
                State = store.LoadIdentity()!;
            }

            key = OpenOrCreateKey();
            EnsureSigningRequest();
        }
        catch
        {
            key?.Dispose();

            if (ownsStore)
                store.Dispose();

            throw;
        }
    }

    private IdentityState LoadOrCreateState(string server, string deviceName)
    {
        var saved = store.LoadIdentity();

        if (saved is null)
        {
            var state = new IdentityState(
                1,
                server,
                deviceName,
                Guid.NewGuid(),
                $"{KeyNamePrefix}{Guid.NewGuid().ToString("N")}");
            Save(state);

            return state;
        }

        if (saved.SchemaVersion != 1 || saved.InstallationId == Guid.Empty || saved.ServerUrl != server
            ||
            string.IsNullOrWhiteSpace(saved.KeyName)
            || !saved.KeyName.StartsWith(KeyNamePrefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Agent identity is invalid or belongs to another server. Restore its original configuration.");
        }

        return saved;
    }

    private RSACng OpenOrCreateKey(string? keyName = null)
    {
        keyName ??= State.KeyName;
        if (!keyName.StartsWith(KeyNamePrefix, StringComparison.Ordinal)) throw new InvalidDataException("Invalid agent key name.");
        if (!CngKey.Exists(keyName))
        {
            if (keyName == State.KeyName ? State.CsrPem is not null || State.CertificatePem is not null : State.PendingCsrPem is not null)
            {
                throw new InvalidDataException("The agent's persisted private key is missing. Restore the original Windows identity; do not reenroll automatically.");
            }

            var parameters = new CngKeyCreationParameters
            {
                ExportPolicy = CngExportPolicies.None,
                KeyUsage = CngKeyUsages.Signing
            };
            parameters.Parameters.Add(new CngProperty(
                "Length",
                BitConverter.GetBytes(ClientKeySizeBits),
                CngPropertyOptions.None));
            using var created = CngKey.Create(CngAlgorithm.Rsa, keyName, parameters);
        }

        using var persisted = CngKey.Open(keyName);

        return new RSACng(persisted);
    }

    private void EnsureSigningRequest()
    {
        if (State.CsrPem is not null)
        {
            return;
        }

        var request = new CertificateRequest("CN=CloneDeploy Agent", key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        Save(State with
        {
            CsrPem = request.CreateSigningRequestPem()
        });
    }

    internal void RefreshEnrollmentName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > AgentOptions.MaximumDeviceNameLength || name.Any(char.IsControl))
            throw new InvalidOperationException("Invalid enrollment computer name.");
        lock (identityLock)
        {
            if (State.CertificatePem is null && State.DeviceName != name)
                Save(State with { DeviceName = name });
        }
    }

    public void AcceptCertificate(Guid deviceGuid, string certificatePem, string caCertificatePem)
    {
        lock (identityLock)
        {
            using var validated = ValidateCertificate(deviceGuid, certificatePem, caCertificatePem);
            Save(State with
            {
                DeviceGuid = deviceGuid,
                CertificatePem = certificatePem,
                CaCertificatePem = caCertificatePem,
                RequestReset = false
            });
        }
    }

    public void ResetEnrollment(AgentOptions options)
    {
        lock (identityLock)
        {
            store.ResetIdentity(options, fullReset: false);
            key.Dispose();
            State = store.LoadIdentity()!;
            key = OpenOrCreateKey();
            EnsureSigningRequest();
        }
    }

    public string? PrepareRenewal()
    {
        lock (identityLock)
        {
            if (!AgentCertificateLifecycle.NeedsRenewal(State.CertificatePem)) return null;
            if (State.PendingKeyName is null)
                Save(State with { PendingKeyName = $"{KeyNamePrefix}{Guid.NewGuid():N}" });
            using var pending = OpenOrCreateKey(State.PendingKeyName);
            if (State.PendingCsrPem is null)
            {
                var request = new CertificateRequest("CN=CloneDeploy Agent", pending, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                Save(State with { PendingCsrPem = request.CreateSigningRequestPem() });
            }
            return State.PendingCsrPem;
        }
    }

    public void AcceptRenewal(CloneDeploy_Agent.Contracts.AgentCertificateRenewal renewal)
    {
        lock (identityLock)
        {
            AgentCertificateLifecycle.ValidateReplacement(State.CertificatePem!, State.CaCertificatePem!, renewal);
            if (State.PendingKeyName is null || State.PendingCsrPem is null)
                throw new InvalidDataException("No pending renewal key.");
            var pending = OpenOrCreateKey(State.PendingKeyName);
            try
            {
                using var validated = ValidateCertificate(State.DeviceGuid!.Value, renewal.CertificatePem, renewal.CaCertificatePem, pending);
                Save(State with { PreviousKeyName = State.KeyName, KeyName = State.PendingKeyName,
                    CsrPem = State.PendingCsrPem, CertificatePem = renewal.CertificatePem,
                    PendingKeyName = null, PendingCsrPem = null });
            }
            catch { pending.Dispose(); throw; }
            var old = key;
            key = pending;
            old.Dispose();
        }
    }

    // Called only after a successful authenticated check-in with the current identity.
    public void CompleteRenewal()
    {
        lock (identityLock)
        {
            if (State.PreviousKeyName is not { } previous) return;
            AgentStore.DeleteIdentityKey(previous);
            Save(State with { PreviousKeyName = null });
        }
    }

    public X509Certificate2? GetClientCertificate()
    {
        lock (identityLock)
        {
            if (State.CertificatePem is null)
            {
                if (State.DeviceGuid is not null || State.CaCertificatePem is not null)
                {
                    throw new InvalidDataException("Incomplete agent certificate state.");
                }

                return null;
            }

            using var certificate = ValidateCertificate(
                State.DeviceGuid ?? Guid.Empty,
                State.CertificatePem,
                State.CaCertificatePem ?? "");

            return certificate.CopyWithPrivateKey(key);
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

    private void Save(IdentityState state)
    {
        store.SaveIdentity(state);
        State = state;
    }

    public void Dispose()
    {
        key.Dispose();

        if (ownsStore)
            store.Dispose();
    }
}
