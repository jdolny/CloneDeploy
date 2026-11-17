using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;

namespace CloneDeploy_Server.Backend.Services.Security.Secrets;

public static class ServerSecretConfiguration
{
    public static void Configure(IConfiguration configuration, IDataProtectionBuilder protection, bool required = false)
    {
        var thumbprint = configuration["ServerSecrets:CertificateThumbprint"];
        var encryptedPassword = configuration["ServerSecrets:DatabasePassword"];
        if (string.IsNullOrWhiteSpace(thumbprint) && string.IsNullOrWhiteSpace(encryptedPassword))
        {
            if (required) throw new InvalidOperationException("The Windows installation requires certificate-protected server secrets.");
            return; // Explicitly unconfigured manual/Linux deployments use their own configuration.
        }

        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("Windows certificate protection requires Windows.");
        if (string.IsNullOrWhiteSpace(thumbprint) || string.IsNullOrWhiteSpace(encryptedPassword))
            throw new InvalidOperationException("Server secret protection configuration is incomplete.");

        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        // A dedicated encryption certificate is pinned, not validated as a TLS identity.
        // Keep expired certificates usable so expiration cannot strand stored secrets.
        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
        var certificate = matches.OfType<X509Certificate2>().FirstOrDefault(c => c.HasPrivateKey)
            ?? throw new InvalidOperationException("The server encryption certificate or private key is missing.");
        Configure(configuration, protection, certificate, encryptedPassword);
    }

    public static void Configure(IConfiguration configuration, IDataProtectionBuilder protection,
        X509Certificate2 certificate, string encryptedPassword)
    {
        var connection = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("CloneDeploy-Server-Database"));
        if (!string.IsNullOrEmpty(connection.Password))
            throw new InvalidOperationException("Certificate-protected configuration must not contain a plaintext database password.");
        try
        {
            var message = new EnvelopedCms();
            message.Decode(Convert.FromBase64String(encryptedPassword));
            // Explicit key avoids searching another user's certificate store.
            using var privateKey = certificate.GetRSAPrivateKey()
                ?? throw new CryptographicException();
            message.Decrypt(message.RecipientInfos[0], privateKey);
            connection.Password = Encoding.UTF8.GetString(message.ContentInfo.Content);
            if (string.IsNullOrEmpty(connection.Password))
                throw new CryptographicException();
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or IndexOutOfRangeException)
        {
            // Do not include the connection string, encrypted payload or underlying exception.
            throw new InvalidOperationException("Unable to decrypt the database credential with the configured server certificate.");
        }

        // Other consumers (including database notifications) use IConfiguration too.
        // This changes configuration in memory; it does not write server.json.
        configuration["ConnectionStrings:CloneDeploy-Server-Database"] = connection.ConnectionString;
        protection.SetApplicationName("CloneDeploy-Server")
            .ProtectKeysWithCertificate(certificate)
            .UnprotectKeysWithAnyCertificate(certificate);
    }
}
