using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed record CertificateExpiryNotice(string Key, string Name, string Href, DateTime? ExpiresUtc, bool Expired)
{
    public DashboardAttentionDto DashboardNotice() => new(Key, $"Dashboard.Attention.{Key}",
        "Dashboard.Attention.CertificateExpiry." + (ExpiresUtc is null ? "Unavailable" : Expired ? "Expired" : "Soon"),
        Href, Expired ? DashboardAttentionSeverity.Error : DashboardAttentionSeverity.Warning,
        Args: [ExpiresUtc?.ToString("yyyy-MM-dd HH:mm 'UTC'") ?? ""]);
}

// Read locally configured credentials only; never probe arbitrary URLs or disclose key material.
public sealed class CertificateExpiryNotices(IConfiguration configuration, IWebHostEnvironment environment, EntraCredentials entra)
{
    public async Task<List<CertificateExpiryNotice>> ReadAsync(CloneDeployDbContext db, DateTime now, bool includeManaged = true, CancellationToken ct = default)
    {
        var result = new List<CertificateExpiryNotice>();
        void Add(string key, string name, string href, DateTime? expires)
        {
            if (expires is null || expires <= now.AddDays(30))
                result.Add(new(key, name, href, expires, expires <= now));
        }
        if (includeManaged)
        {
            var rows = await db.AppCertificates.AsNoTracking().Where(x => !x.IsRevoked &&
                (x.Type == CertificateType.Authority || x.Type == CertificateType.Intermediate || x.Type == CertificateType.Web) &&
                x.NotAfterUtc <= now.AddDays(30)).Select(x => new { x.Type, x.NotAfterUtc }).ToListAsync(ct);
            foreach (var row in rows)
                Add("ManagedCertificate", row.Type == CertificateType.Web ? "Main server HTTPS" : "Installation CA", "/administration/server/certificates", row.NotAfterUtc);
        }
        var active = await db.EntraConnections.AsNoTracking().Select(x => x.CertificateEncrypted).SingleOrDefaultAsync(ct);
        if (active is not null)
        {
            DateTime? expires = null;
            try { using var certificate = entra.Open(active); expires = certificate.NotAfter.ToUniversalTime(); }
            catch (Exception ex) when (ex is CryptographicException or FormatException or InvalidOperationException or ArgumentException or IOException) { }
            Add("EntraCertificate", "Microsoft Entra application certificate", "/administration/server/entra", expires);
        }
        var external = configuration.GetSection("Kestrel:Certificates:Default");
        if (!configuration.GetValue("Certificates:UseManagedHttps", true) && external.GetChildren().Any())
        {
            DateTime? expires = null;
            try { using var certificate = ReadExternal(external, now); expires = certificate.NotAfter.ToUniversalTime(); }
            catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { }
            Add("ExternalHttpsCertificate", "Configured external HTTPS certificate", "/administration/server/certificates", expires);
        }
        return result;
    }

    private X509Certificate2 ReadExternal(IConfigurationSection settings, DateTime now)
    {
        if (settings["Path"] is { Length: > 0 } path)
        {
            path = Path.GetFullPath(path, environment.ContentRootPath);
            // A separate key file denotes PEM configuration. Only the public certificate is needed.
            if (!string.IsNullOrWhiteSpace(settings["KeyPath"])) return X509Certificate2.CreateFromPem(File.ReadAllText(path));
            return X509CertificateLoader.LoadPkcs12FromFile(path, settings["Password"], X509KeyStorageFlags.EphemeralKeySet);
        }
        if (settings["Subject"] is { Length: > 0 } subject)
        {
            var store = settings["Store"] ?? "My";
            var location = System.Enum.Parse<StoreLocation>(settings["Location"] ?? "CurrentUser", true);
            var allowInvalid = settings.GetValue("AllowInvalid", false);
            try
            {
                return Microsoft.AspNetCore.Server.Kestrel.Https.CertificateLoader.LoadFromStoreCert(subject, store, location, allowInvalid);
            }
            catch (InvalidOperationException) when (!allowInvalid)
            {
                // An expired certificate is rejected by Kestrel's normal store lookup.
                // Retain that expiry for an actionable error; other invalid credentials remain unreadable.
                var candidate = Microsoft.AspNetCore.Server.Kestrel.Https.CertificateLoader.LoadFromStoreCert(subject, store, location, allowInvalid: true);
                if (candidate.NotAfter.ToUniversalTime() <= now) return candidate;
                candidate.Dispose();
                throw;
            }
        }
        throw new InvalidOperationException("No configured certificate source.");
    }
}
