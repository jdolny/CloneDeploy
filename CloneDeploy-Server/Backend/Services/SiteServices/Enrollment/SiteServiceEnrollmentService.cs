using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CloneDeploy.SiteServices;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed class SiteServiceEnrollmentService(ICloneDeployDbFactory factory, ISecretProtector secrets, TimeProvider clock, IConfiguration? configuration = null) : ISiteProxyTrust, ISiteProxyAccessPolicy
{
    public WindowsWorkerPolicy WorkerPolicy { get; init; } = new();
    public async Task<SiteEnrollmentReply> EnrollAsync(SiteEnrollmentRequest request, CancellationToken ct)
    {
        if (request.HostOs is not (null or "Windows" or "Linux" or "Other")) throw new InvalidDataException("Invalid host OS.");
        if (request.Hostname is null || request.Hostname.Length is < 1 or > 255 || request.Version?.Length > 100 ||
            request.Addresses is null || request.Addresses.Length > 32 || request.Addresses.Any(x => x is null || x.Length > 64) || request.Token?.Length > 256)
            throw new InvalidDataException("Invalid enrollment details.");
        SiteEnrollmentCrypto.Verify(request, clock.GetUtcNow());
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(ct); await SiteServiceAdministration.Lock(db, ct);
        var row = await db.SiteServices.Include(x => x.Issuer).SingleOrDefaultAsync(x => x.Id == request.RegistrationId, ct)
            ?? throw new UnauthorizedAccessException();
        if (!IssuerActive(row) || row.State == SiteRegistrationState.Revoked) throw new UnauthorizedAccessException();
        if (row.IsWindowsWorker != request.IsWindowsWorker || row.IsWindowsWorker && (!WorkerPolicy.RemoteAllowed || request.HostOs != "Windows"))
            throw new UnauthorizedAccessException();
        var fingerprint = SiteEnrollmentCrypto.KeyFingerprint(SiteEnrollmentCrypto.ReadCsr(request.IdentityCsr));
        if (row.State == SiteRegistrationState.Created)
        {
            if (request.Token is null || row.TokenHash is null || row.TokenExpiresAtUtc <= clock.GetUtcNow().UtcDateTime ||
                !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(row.TokenHash), Convert.FromHexString(SiteServiceAdministration.Hash(request.Token))))
                throw new UnauthorizedAccessException();
            row.TokenHash = null; // The token is consumed atomically; only this key may retry/poll.
            row.KeyFingerprint = fingerprint; row.IdentityCsr = request.IdentityCsr; row.HttpsCsr = request.HttpsCsr;
            row.Hostname = request.Hostname; row.Version = request.Version;
            row.Addresses = string.Join(", ", request.Addresses); row.State = SiteRegistrationState.Pending; row.Revision++;
        }
        else if (row.KeyFingerprint != fingerprint || row.IdentityCsr != request.IdentityCsr || row.HttpsCsr != request.HttpsCsr)
            throw new UnauthorizedAccessException();
        if (row.State == SiteRegistrationState.Approved && row.CertificateExpiresAtUtc <= clock.GetUtcNow().UtcDateTime)
            throw new UnauthorizedAccessException();
        if (request.HostOs is not null) row.HostOs = request.HostOs;
        row.LastContactAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        var access = await SiteServiceAdministration.ReadProxyAccessAsync(db, row.Id, ct);
        return new(row.State.ToString(), row.State == SiteRegistrationState.Approved
            ? Configuration(row) with { AuthenticationRequestsPerMinute = await SiteServiceAdministration.ReadProxyRateAsync(db, row.Id, ct),
            PortalPort = ListenerPorts.Read(configuration ?? new ConfigurationBuilder().Build()).PortalPort,
            Access = access, ImagingEnabled = access.ImagingEnabled, ImagingLoginRequestsPerMinute = access.ImagingLoginRequestsPerMinute,
            AgentProvisioningKeyHash = await AgentEnrollmentService.ReadProvisioningKeyHashAsync(db, ct) } : null);
    }

    public async Task<SiteConfiguration> StatusAsync(X509Certificate2 certificate, SiteStatusRequest request, CancellationToken ct)
    {
        if (request.Boot?.Error?.Length > 1000 || request.Boot?.AppliedRevision < 0 || request.Boot?.ActiveTransfers < 0 || request.Error?.Length > 1000 || request.Version?.Length > 100 || request.AppliedRevision < 0)
            throw new InvalidDataException("Invalid service status.");
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(ct); await SiteServiceAdministration.Lock(db, ct);
        var hash = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        var row = await db.SiteServices.Include(x => x.Issuer).SingleOrDefaultAsync(x => x.CertificateSha256 == hash || x.PreviousCertificateSha256 == hash, ct);
        if (row is null || !IdentityActive(row, certificate, hash) || row.IsWindowsWorker && (!WorkerPolicy.RemoteAllowed || request.HostOs != "Windows" || request.ProxyRunning || request.Boot is not null)) throw new UnauthorizedAccessException();
        row.LastContactAtUtc = clock.GetUtcNow().UtcDateTime; row.AppliedRevision = request.AppliedRevision;
        row.BootStatusJson = request.Boot is null ? null : System.Text.Json.JsonSerializer.Serialize(request.Boot);
        if (request.HostOs is not (null or "Windows" or "Linux" or "Other")) throw new InvalidDataException("Invalid host OS.");
        row.HostOs = request.HostOs;
        row.ProxyRunning = request.ProxyRunning; row.LastError = request.Error; row.Version = request.Version;
        if (row.CertificateExpiresAtUtc < clock.GetUtcNow().AddDays(30).UtcDateTime && row.Issuer.NotAfterUtc > clock.GetUtcNow().AddDays(31).UtcDateTime)
        {
            using var root = WebCertificateService.Load(row.Issuer, secrets);
            SiteServiceCertificates.Issue(row, root, clock.GetUtcNow()); row.Revision++;
        }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        var access = await SiteServiceAdministration.ReadProxyAccessAsync(db, row.Id, ct);
        return Configuration(row) with { AuthenticationRequestsPerMinute = await SiteServiceAdministration.ReadProxyRateAsync(db, row.Id, ct),
            PortalPort = ListenerPorts.Read(configuration ?? new ConfigurationBuilder().Build()).PortalPort,
            Access = access, ImagingEnabled = access.ImagingEnabled, ImagingLoginRequestsPerMinute = access.ImagingLoginRequestsPerMinute,
            AgentProvisioningKeyHash = await AgentEnrollmentService.ReadProvisioningKeyHashAsync(db, ct) };
    }

    public async Task<Guid> AuthorizeStorageAsync(X509Certificate2 certificate, CancellationToken ct)
        => await AuthorizeIdentityAsync(certificate, false, ct);

    public async Task<Guid> AuthorizeWorkerAsync(X509Certificate2 certificate, CancellationToken ct)
    {
        if (!WorkerPolicy.RemoteAllowed) throw new UnauthorizedAccessException();
        return await AuthorizeIdentityAsync(certificate, true, ct);
    }

    private async Task<Guid> AuthorizeIdentityAsync(X509Certificate2 certificate, bool worker, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        var hash = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        var row = await db.SiteServices.AsNoTracking().Include(x => x.Issuer)
            .SingleOrDefaultAsync(x => x.CertificateSha256 == hash || x.PreviousCertificateSha256 == hash, ct);
        if (row is null || row.IsWindowsWorker != worker || worker && row.WindowsWorkerRoles != 7 || !IdentityActive(row, certificate, hash)) throw new UnauthorizedAccessException();
        return row.Id;
    }

    public async Task AuthorizeBootAsync(X509Certificate2 certificate, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        var hash = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        var row = await db.SiteServices.AsNoTracking().Include(x => x.Issuer)
            .SingleOrDefaultAsync(x => x.CertificateSha256 == hash || x.PreviousCertificateSha256 == hash, ct);
        if (row is null || row.IsWindowsWorker || !IdentityActive(row, certificate, hash)) throw new UnauthorizedAccessException();
        var boot = System.Text.Json.JsonSerializer.Deserialize<SiteBootSettings>(row.BootSettingsJson) ?? new();
        if (!boot.PxeEnabled && !boot.TftpEnabled) throw new UnauthorizedAccessException();
    }

    public async ValueTask<bool?> IsTrustedAsync(X509Certificate2 certificate, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        var hash = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        var row = await db.SiteServices.AsNoTracking().Include(x => x.Issuer)
            .SingleOrDefaultAsync(x => x.CertificateSha256 == hash || x.PreviousCertificateSha256 == hash, ct);
        return row is null ? null : !row.IsWindowsWorker && row.ProxyEnabled && IdentityActive(row, certificate, hash);
    }

    public async ValueTask<bool> AllowsAsync(X509Certificate2 certificate, Microsoft.AspNetCore.Http.PathString path, System.Net.IPAddress address, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        var hash = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        var id = await db.SiteServices.Where(x => x.CertificateSha256 == hash || x.PreviousCertificateSha256 == hash).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(ct);
        // Unmanaged pinned proxies cannot enable administrator access.
        return id is { } site ? (await SiteServiceAdministration.ReadProxyAccessAsync(db, site, ct)).Allows(path, address)
            : new SiteProxyAccess().Allows(path, address);
    }
    public async Task<Guid> AuthorizeCapabilityAsync(X509Certificate2 certificate, bool multicast, CancellationToken ct)
    {
        var site = await AuthorizeStorageAsync(certificate, ct);
        await using var db = await factory.CreateContextAsync();
        var access = await SiteServiceAdministration.ReadProxyAccessAsync(db, site, ct);
        if (!(multicast ? access.MulticastEnabled && access.StorageEnabled : access.StorageEnabled)) throw new UnauthorizedAccessException();
        return site;
    }

    private bool IssuerActive(SiteServiceRegistration row) => !row.Issuer.IsRevoked &&
        row.Issuer.NotBeforeUtc <= clock.GetUtcNow().UtcDateTime && row.Issuer.NotAfterUtc > clock.GetUtcNow().UtcDateTime;
    private bool IdentityActive(SiteServiceRegistration row, X509Certificate2 certificate, string hash) =>
        row.State == SiteRegistrationState.Approved && IssuerActive(row) && SiteProxyProtocol.IsCurrentClientCertificate(certificate, clock.GetUtcNow()) &&
        (row.CertificateSha256 == hash || row.PreviousCertificateSha256 == hash && row.PreviousCertificateUntilUtc > clock.GetUtcNow().UtcDateTime);
    private static SiteConfiguration Configuration(SiteServiceRegistration row) => new(row.Id, row.Revision, row.ProxyEnabled,
        row.ListenAddress, row.PublicUrl, row.BackendUrl,
        System.Security.Cryptography.PemEncoding.WriteString("CERTIFICATE", row.IdentityCertificate!),
        System.Security.Cryptography.PemEncoding.WriteString("CERTIFICATE", row.HttpsCertificate!))
        { IsWindowsWorker = row.IsWindowsWorker, Boot = row.IsWindowsWorker ? new() : System.Text.Json.JsonSerializer.Deserialize<SiteBootSettings>(row.BootSettingsJson) ?? new(), WindowsWorkerRoles = row.IsWindowsWorker ? (WindowsWorkerRoles)row.WindowsWorkerRoles : WindowsWorkerRoles.None };
}
