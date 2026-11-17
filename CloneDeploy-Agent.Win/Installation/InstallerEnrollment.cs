using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloneDeploy_Agent;

/// <summary>Applies configured MSI enrollment under the service identity; unconfigured upgrades preserve settings.</summary>
public static class InstallerEnrollment
{
    private const int MaximumEncodedCaLength = 90_000;

    private sealed record EnrollmentFile(int SchemaVersion, string ServerUrl, string ProvisioningKey, string CaCertificate);

    public static void ImportFile(AgentStore store, AgentOptions options, string path)
    {
        path = Path.GetFullPath(path);
        if (new Uri(path).IsUnc || new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Network)
            throw new InvalidDataException("Copy enrollment.json to a local disk before installing.");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is <= 0 or > 131072) throw new InvalidDataException("Invalid enrollment file size.");
        EnrollmentFile configuration;
        try
        {
            configuration = JsonSerializer.Deserialize<EnrollmentFile>(input, new JsonSerializerOptions
            { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
                ?? throw new InvalidDataException("Missing enrollment configuration.");
        }
        catch (JsonException) { throw new InvalidDataException("Invalid enrollment JSON."); }
        if (configuration.SchemaVersion != 1 || string.IsNullOrWhiteSpace(configuration.CaCertificate))
            throw new InvalidDataException("Enrollment requires schema version 1 and a CloneDeploy CA.");
        Save(store, options, configuration.ServerUrl, configuration.ProvisioningKey, configuration.CaCertificate);
    }

    public static void Save(
        AgentStore store,
        AgentOptions options,
        string? serverUrl,
        string? provisioningKey,
        string? caCertificate)
    {
        if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(provisioningKey))
            throw new InvalidDataException("The MSI must contain a server URL and provisioning key.");

        if (provisioningKey.Length != 64 || !provisioningKey.All(Uri.IsHexDigit))
            throw new InvalidDataException("The MSI provisioning key must contain 64 hexadecimal characters.");

        var enrollment = new AgentOptions
        {
            StateDirectory = options.StateDirectory,
            ServerUrl = serverUrl,
            ProvisioningKey = provisioningKey,
            DeviceName = store.GetSetting("DeviceName") ?? Environment.MachineName
        };
        _ = enrollment.GetServerUri();

        byte[]? caBytes = null;
        if (string.IsNullOrWhiteSpace(caCertificate))
            throw new InvalidDataException("The MSI must contain the trusted CloneDeploy CA certificate.");
        if (!string.IsNullOrWhiteSpace(caCertificate))
        {
            if (caCertificate.Length > MaximumEncodedCaLength)
                throw new InvalidDataException("Enrollment CA is too large.");

            using var certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(caCertificate));

            if (certificate.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault()?.CertificateAuthority != true)
                throw new InvalidDataException("Enrollment certificate must be a public CA certificate.");

            caBytes = certificate.RawData;
        }

        var previousPath = store.GetSetting("ServerCaCertificatePath");
        byte[]? previousCa = null;
        if (!string.IsNullOrWhiteSpace(previousPath) && File.Exists(previousPath))
        {
            try { using var previous = X509CertificateLoader.LoadCertificateFromFile(previousPath); previousCa = previous.RawData; }
            catch (CryptographicException) { /* Reinstall repairs an unreadable trust certificate. */ }
        }
        var trustChanged = caBytes is null ? !string.IsNullOrWhiteSpace(previousPath)
            : previousCa is null || !caBytes.AsSpan().SequenceEqual(previousCa);
        if (caBytes is not null)
        {
            // Stage independently of the currently configured CA. A failed settings transaction
            // must not change the trust certificate used by the existing configuration.
            enrollment.ServerCaCertificatePath = Path.Combine(options.StateDirectory, $"server-ca-{Convert.ToHexString(SHA256.HashData(caBytes))}.cer");
            var temporary = enrollment.ServerCaCertificatePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllBytes(temporary, caBytes); File.Move(temporary, enrollment.ServerCaCertificatePath, overwrite: true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        store.ApplyInstallerConfiguration(enrollment, trustChanged);
    }
}
