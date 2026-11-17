using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Manages agent enrollment settings, approvals, and identity certificates.
/// </summary>
public sealed partial class AgentEnrollmentService(ICloneDeployDbFactory factory, ISecretProtector secrets,
    IAuthorizationService authorization, TimeProvider clock)
{
    private const string SettingsKey = "AgentProvisioning.Configuration";
    private sealed record StoredConfiguration(AgentProvisioningOptionsDto Options, string? KeyHash, int ConfiguredByUserId)
    {
        public string? KeyEncrypted { get; init; }
    }

    public async Task<AgentProvisioningConfigurationDto> GetConfigurationAsync(ClaimsPrincipal user)
    {
        await RequireAdmin(user);
        await using var db = await factory.CreateContextAsync();
        var settings = await ReadSettings(db, default);
        return new(settings.Options, settings.KeyHash is not null);
    }

    public async Task SaveConfigurationAsync(ClaimsPrincipal user, AgentProvisioningOptionsDto options)
    {
        var userId = await RequireAdmin(user);
        if (options.PendingRetentionDays is < 1 or > 3650)
            throw new AgentEnrollmentException(400, "Pending request retention must be between 1 and 3650 days.");
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        await Lock(db, default);
        var stored = await ReadSettings(db, default);
        await WriteSettings(db, stored with { Options = options, ConfiguredByUserId = userId });
        db.AuditLogs.Add(Audit(userId, user.Identity?.Name, "ProvisioningSettings", "Agent provisioning", options, AuditType.Update));
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    public async Task<string> GenerateProvisioningKeyAsync(ClaimsPrincipal user, bool replaceExisting = false)
    {
        var userId = await RequireAdmin(user);
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        await Lock(db, default);
        var stored = await ReadSettings(db, default);
        if (stored.KeyHash is not null && !replaceExisting) throw new AgentEnrollmentException(409, "A provisioning key already exists. Use Rotate key to replace it.");
        var now = clock.GetUtcNow().UtcDateTime;
        if (!await db.AppCertificates.AnyAsync(x => x.Type == CertificateType.Authority && !x.IsRevoked && x.NotBeforeUtc <= now && x.NotAfterUtc > now.AddDays(1)))
            throw new AgentEnrollmentException(409, "Create a valid certificate authority first.");
        var raw = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await WriteSettings(db, stored with { KeyHash = Hash(raw), KeyEncrypted = secrets.Protect(raw), ConfiguredByUserId = userId });
        db.AuditLogs.Add(Audit(userId, user.Identity?.Name, "ProvisioningKey", "Agent provisioning", new { Rotated = stored.KeyHash is not null }, AuditType.Update));
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return raw;
    }

    public async Task<string> GetInstallerKeyAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        if (!AgentReleaseStartupPrincipal.IsStartup(user)) await RequireAdmin(user);
        await using var db = await factory.CreateContextAsync();
        var stored = await ReadSettings(db, ct);
        if (stored.KeyHash is null) throw new InvalidDataException("Generate a provisioning key in the Enrollment tab first.");
        if (string.IsNullOrEmpty(stored.KeyEncrypted))
            throw new InvalidDataException("The existing provisioning key was stored as a hash only. Rotate it once in the Enrollment tab to enable installer generation; pending agents will need the replacement key.");
        var key = secrets.Unprotect(stored.KeyEncrypted);
        if (key.Length != 64 || !key.All(Uri.IsHexDigit) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(key)), Encoding.ASCII.GetBytes(stored.KeyHash)))
            throw new InvalidDataException("The saved provisioning key is inconsistent. Rotate it in the Enrollment tab.");
        return key;
    }

    public async Task<List<AgentProvisioningRowDto>> ListAsync(ClaimsPrincipal user)
    {
        await RequireApprovalPermission(user);
        await using var db = await factory.CreateContextAsync();
        return await db.AgentProvisioningRequests.AsNoTracking().Where(x => x.State != AgentProvisioningState.Provisioned)
            .OrderBy(x => x.RequestedAtUtc)
            .Select(x => new AgentProvisioningRowDto(x.Id, x.DeviceName, x.InstallationId, x.PublicKeyHash, x.IpAddress, x.RequestedAtUtc, x.State)
            { HardwareUuid = x.HardwareUuid, MacAddresses = x.MacAddresses, LastSeenAtUtc = x.LastSeenAtUtc }).ToListAsync();
    }

    public Task DecideAsync(ClaimsPrincipal user, Guid id, bool approve) => DecideAsync(user, id, approve, false);

    public Task ApprovePendingAsync(ClaimsPrincipal user, Guid id) => DecideAsync(user, id, true, true);

    private async Task DecideAsync(ClaimsPrincipal user, Guid id, bool approve, bool pendingApprovalOnly)
    {
        var userId = await RequireApprovalPermission(user);
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        await Lock(db, default);
        var pending = await db.AgentProvisioningRequests.SingleOrDefaultAsync(x => x.Id == id)
            ?? throw new AgentEnrollmentException(404, "Provisioning request not found.");
        if (pendingApprovalOnly && pending.State != AgentProvisioningState.PendingApproval)
            throw new AgentEnrollmentException(409, "This request is no longer waiting for enrollment approval. Refresh and review its current status.");
        if (pending.State == AgentProvisioningState.Provisioned) throw new AgentEnrollmentException(409, "This device is already provisioned. Archive it to block access.");
        if (approve)
        {
            if (pending.State == AgentProvisioningState.PendingPreProvision)
                throw new AgentEnrollmentException(409, "Create or sync the device record and wait for the agent to retry before approving enrollment.");
            var isReset = pending.State is AgentProvisioningState.PendingReset or AgentProvisioningState.ResetApproved or AgentProvisioningState.ResetDenied;
            var device = isReset
                ? await FindResetDevice(db, pending.DeviceName, pending.InstallationId, default)
                : await FindEnrollmentDevice(db, pending.DeviceName, pending.InstallationId, pending, pending.HardwareUuid, pending.MacAddresses, default);
            if (isReset && device is not null && await db.AgentProvisioningRequests.AnyAsync(x =>
                x.Id != pending.Id && x.TargetDeviceId == device.Id && x.State == AgentProvisioningState.ResetApproved))
                throw new AgentEnrollmentException(409, "Another reset is already approved for this device.");
            if (device is null)
            {
                if ((await ReadSettings(db, default)).Options.RequirePreProvision)
                    throw new AgentEnrollmentException(409, "An existing device record is required before enrollment can be approved.");
                device = new Device { Name = pending.DeviceName, Guid = Guid.NewGuid(), ProvisionStatus = DeviceProvisionStatus.PreProvisioned };
                db.Devices.Add(device);
            }
            // Reserve for the exact installation/public key, not every caller claiming the hostname.
            pending.TargetDevice = device;
            pending.ApprovedAtUtc = clock.GetUtcNow().UtcDateTime;
            pending.ApprovedByUserId = userId;
            pending.State = isReset ? AgentProvisioningState.ResetApproved : AgentProvisioningState.Approved;
        }
        else
        {
            var isReset = pending.State is AgentProvisioningState.PendingReset or AgentProvisioningState.ResetApproved or AgentProvisioningState.ResetDenied;
            pending.State = isReset ? AgentProvisioningState.ResetDenied : AgentProvisioningState.Denied;
            pending.ApprovedAtUtc = null;
            pending.ApprovedByUserId = null;
            pending.TargetDeviceId = null;
        }
        db.AuditLogs.Add(Audit(userId, user.Identity?.Name, "AgentProvisioningDecision", pending.DeviceName,
            new { pending.Id, pending.InstallationId, pending.PublicKeyHash, Approved = approve }, approve ? AuditType.ApproveProvision : AuditType.Update));
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    public async Task<AgentEnrollmentResponseDto> EnrollAsync(AgentEnrollmentRequestDto input, CancellationToken ct = default, string? remoteIp = null)
    {
        if (input.ProvisioningKey is null || input.ProvisioningKey.Length != 64 || !input.ProvisioningKey.All(Uri.IsHexDigit))
            throw new AgentEnrollmentException(401, "Invalid provisioning key.");
        var name = input.DeviceName?.Trim() ?? "";
        if (input.HardwareUuid?.Length > 500 || input.MacAddresses?.Length > 64 || input.MacAddresses?.Any(x => x is null || x.Length > 64) == true)
            throw new AgentEnrollmentException(400, "Hardware identity is too large.");
        if (name.Length is < 1 or > 128 || name.Any(char.IsControl) || input.InstallationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(input.CsrPem) || input.CsrPem.Length > 16_384)
            throw new AgentEnrollmentException(400, "A device name, installation ID and PEM certificate request are required.");
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Settings, approvals, key rotation and enrollment share this cross-instance lock.
        await Lock(db, ct);
        var settings = await ReadSettings(db, ct);
        if (settings.KeyHash is null || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(input.ProvisioningKey)), Encoding.ASCII.GetBytes(settings.KeyHash)))
            throw new AgentEnrollmentException(401, "Invalid provisioning key.");
        CertificateRequest csr;
        string publicKeyHash;
        try
        {
            csr = CertificateRequest.LoadSigningRequestPem(input.CsrPem, HashAlgorithmName.SHA256,
                CertificateRequestLoadOptions.Default, RSASignaturePadding.Pkcs1);
            using var rsa = csr.PublicKey.GetRSAPublicKey();
            if (rsa is null || rsa.KeySize is < 2048 or > 4096) throw new CryptographicException();
            publicKeyHash = Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()));
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        { throw new AgentEnrollmentException(400, "Provide a valid, signed RSA certificate request with a 2048â€“4096 bit key."); }
        var now = clock.GetUtcNow();
        var pending = await db.AgentProvisioningRequests.Include(x => x.AgentCertificate)!.ThenInclude(x => x!.Device)
            .Include(x => x.AgentCertificate)!.ThenInclude(x => x!.Issuer)
            .SingleOrDefaultAsync(x => x.InstallationId == input.InstallationId, ct);
        Device? device;
        // A retry after certificate issuance returns the same certificate, even if the client
        // has not yet saved the response and still sends RequestReset.
        var completedResetRetry = pending?.State == AgentProvisioningState.Provisioned
            && pending.PublicKeyHash == publicKeyHash;
        if (input.RequestReset && !completedResetRetry)
        {
            pending = await PrepareResetRequest(db, pending, name, input.InstallationId, publicKeyHash, now.UtcDateTime, ct);
            pending.LastSeenAtUtc = now.UtcDateTime;
            pending.IpAddress = remoteIp;
            SetRequestHardware(pending, input);
            if (settings.Options.AutoApproveResetRequests && pending.State == AgentProvisioningState.PendingReset)
            {
                var target = await FindResetDevice(db, name, input.InstallationId, ct);
                if (target is not null && await db.AgentProvisioningRequests.AnyAsync(x =>
                    x.Id != pending.Id && x.TargetDeviceId == target.Id && x.State == AgentProvisioningState.ResetApproved, ct))
                    throw new AgentEnrollmentException(409, "Another reset is already approved for this device.");
                if (target is null)
                {
                    if (settings.Options.RequirePreProvision)
                        throw new AgentEnrollmentException(409, "An existing device record is required before enrollment can be approved.");
                    target = new Device { Name = name, Guid = Guid.NewGuid(), ProvisionStatus = DeviceProvisionStatus.PreProvisioned };
                    db.Devices.Add(target);
                    pending.TargetDevice = target;
                }
                pending.TargetDevice = target;
                pending.State = AgentProvisioningState.ResetApproved;
                pending.ApprovedAtUtc = now.UtcDateTime;
                pending.ApprovedByUserId = null;
                db.AuditLogs.Add(Audit(settings.ConfiguredByUserId, "Agent provisioning", "AgentResetAutoApproved", name,
                    new { pending.Id, pending.InstallationId }, AuditType.ApproveProvision));
                await db.SaveChangesAsync(ct);
            }
            if (pending.State != AgentProvisioningState.ResetApproved)
            {
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                throw new AgentEnrollmentPendingException(new(1, pending.Id, "PendingReset", 60));
            }
            device = await db.Devices.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == pending.TargetDeviceId, ct);
            if (device is null || device.ArchivedAtUtc is not null)
                throw new AgentEnrollmentException(409, "The approved reset target is unavailable.");
        }
        else
        {
            if (pending?.State is AgentProvisioningState.PendingReset or AgentProvisioningState.ResetApproved or AgentProvisioningState.ResetDenied)
                throw new AgentEnrollmentException(409, "Continue using the reset request until recovery completes.");
            if (pending is not null && pending.PublicKeyHash != publicKeyHash)
                throw new AgentEnrollmentException(409, "This installation ID is already bound to a different public key. Administrator recovery is required.");
            if (pending?.State == AgentProvisioningState.Denied) throw new AgentEnrollmentException(403, "Provisioning was denied by an administrator.");
            if (pending?.State == AgentProvisioningState.Provisioned)
            {
                var previous = pending.AgentCertificate;
                if (previous is null || previous.RevokedAtUtc is not null || previous.RetireAfterUtc <= now.UtcDateTime || previous.Issuer.IsRevoked || previous.Issuer.NotAfterUtc <= now.UtcDateTime ||
                    previous.NotAfterUtc <= now.UtcDateTime || previous.Device.ArchivedAtUtc is not null || previous.Device.ProvisionStatus != DeviceProvisionStatus.Provisioned)
                    throw new AgentEnrollmentException(409, "This installation requires administrator recovery.");
                using var oldIssuer = LoadIssuer(previous.Issuer);
                using var oldCertificate = X509CertificateLoader.LoadCertificate(previous.CertificateDer);
                // Hostnames can change during specialize. Return the existing identity;
                // authenticated check-in updates the device name after the agent saves it.
                pending.DeviceName = name;
                pending.LastSeenAtUtc = now.UtcDateTime;
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return new(1, previous.Device.Guid, oldCertificate.ExportCertificatePem(), oldIssuer.ExportCertificatePem(), previous.NotAfterUtc);
            }
            device = await FindEnrollmentDevice(db, name, input.InstallationId, pending, input.HardwareUuid, input.MacAddresses, ct);
            pending ??= new AgentProvisioningRequest { Id = Guid.NewGuid(), DeviceName = name, InstallationId = input.InstallationId,
                PublicKeyHash = publicKeyHash, RequestedAtUtc = now.UtcDateTime };
            if (db.Entry(pending).State == EntityState.Detached) db.AgentProvisioningRequests.Add(pending);
            pending.DeviceName = name;
            if (device is not null) pending.TargetDevice = device;
            pending.LastSeenAtUtc = now.UtcDateTime;
            pending.IpAddress = remoteIp;
            SetRequestHardware(pending, input);
            var needsPreProvision = settings.Options.RequirePreProvision && device is null;
            var needsApproval = settings.Options.RequirePreProvision ? settings.Options.PreProvisionRequiresApproval : settings.Options.RequireApproval;
            if (needsPreProvision || (needsApproval && pending.ApprovedAtUtc is null && device?.ProvisionStatus != DeviceProvisionStatus.ProvisionApproved))
            {
                pending.State = needsPreProvision ? AgentProvisioningState.PendingPreProvision : AgentProvisioningState.PendingApproval;
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                throw new AgentEnrollmentPendingException(new(1, pending.Id, pending.State.ToString(), 60));
            }
        }
        var authority = await db.AppCertificates.SingleOrDefaultAsync(x => x.Type == CertificateType.Authority, ct);
        if (authority is null || authority.IsRevoked || authority.NotBeforeUtc > now.UtcDateTime || authority.NotAfterUtc <= now.AddDays(1).UtcDateTime)
            throw new AgentEnrollmentException(503, "The certificate authority is unavailable.");
        using var issuer = LoadIssuer(authority);
        device ??= new Device { Name = name, Guid = Guid.NewGuid() };
        if (device.Guid == Guid.Empty) device.Guid = Guid.NewGuid();
        device.InstallationId = input.InstallationId.ToString("D");
        device.Name = name;
        if (Guid.TryParse(input.HardwareUuid, out var hardwareUuid) && hardwareUuid != Guid.Empty &&
            hardwareUuid != Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff")) device.UUID = hardwareUuid.ToString();
        var enrollmentMacs = (input.MacAddresses ?? []).Select(x => x.Replace(":", "").Replace("-", "").Trim().ToUpperInvariant())
            .Where(x => x.Length == 12 && x.All(Uri.IsHexDigit) && x != "000000000000" && (Convert.ToByte(x[..2], 16) & 1) == 0).Distinct().ToArray();
        if (enrollmentMacs.Length > 0)
        {
            var knownMacs = await db.DeviceNics.Where(n => n.DeviceId == device.Id).Select(n => n.Mac).ToListAsync(ct);
            foreach (var mac in enrollmentMacs.Where(mac => !knownMacs.Any(known =>
                known?.Replace(":", "").Replace("-", "").Trim().Equals(mac, StringComparison.OrdinalIgnoreCase) == true)))
                device.Nics.Add(new DeviceNic { Mac = mac, Name = "Enrollment adapter" });
        }
        device.ProvisionStatus = DeviceProvisionStatus.Provisioned;
        device.ProvisionedAtUtc = now.UtcDateTime;
        device.LastKnownIp = remoteIp;
        var subject = new X500DistinguishedNameBuilder();
        subject.AddCommonName(device.Guid.ToString("D"));
        var request = new CertificateRequest(subject.Build(), csr.PublicKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(AgentCertificateTrust.ClientAuthenticationOid) }, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var start = now.AddMinutes(-5) < issuer.NotBefore.ToUniversalTime() ? new DateTimeOffset(issuer.NotBefore.ToUniversalTime()) : now.AddMinutes(-5);
        var end = now.AddYears(1) > issuer.NotAfter.ToUniversalTime() ? new DateTimeOffset(issuer.NotAfter.ToUniversalTime()) : now.AddYears(1);
        using var certificate = request.Create(issuer, start, end, RandomNumberGenerator.GetBytes(20));
        var registered = new AgentCertificate { Id = Guid.NewGuid(), Device = device, Issuer = authority,
            CertificateDer = certificate.RawData, Sha256Fingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256),
            NotBeforeUtc = certificate.NotBefore.ToUniversalTime(), NotAfterUtc = certificate.NotAfter.ToUniversalTime() };
        if (pending.State == AgentProvisioningState.ResetApproved)
        {
            // Revoke old credentials only when the approved replacement is ready to commit.
            var oldCertificates = await db.AgentCertificates.Where(x => x.DeviceId == device.Id && x.RevokedAtUtc == null).ToListAsync(ct);
            foreach (var old in oldCertificates) old.RevokedAtUtc = now.UtcDateTime;
            var otherResets = await db.AgentProvisioningRequests.Where(x => x.Id != pending.Id && x.TargetDeviceId == device.Id
                && (x.State == AgentProvisioningState.PendingReset || x.State == AgentProvisioningState.ResetApproved)).ToListAsync(ct);
            foreach (var other in otherResets)
            {
                other.State = AgentProvisioningState.ResetDenied;
                other.ApprovedAtUtc = null;
                other.ApprovedByUserId = null;
            }
        }
        db.AgentCertificates.Add(registered);
        pending.AgentCertificate = registered;
        pending.TargetDevice = device;
        pending.State = AgentProvisioningState.Provisioned;
        db.AuditLogs.Add(Audit(pending.ApprovedByUserId ?? settings.ConfiguredByUserId, "Agent provisioning", "EnrollAgent", device.Guid.ToString(),
            new { DeviceName = name, pending.InstallationId, registered.Sha256Fingerprint }, AuditType.Create));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(1, device.Guid, certificate.ExportCertificatePem(), issuer.ExportCertificatePem(), registered.NotAfterUtc);
    }

    private static async Task<AgentProvisioningRequest> PrepareResetRequest(CloneDeployDbContext db,
        AgentProvisioningRequest? pending, string name, Guid installationId, string publicKeyHash, DateTime now, CancellationToken ct)
    {
        if (pending?.State is AgentProvisioningState.Denied or AgentProvisioningState.ResetDenied)
            throw new AgentEnrollmentException(403, "This request was denied. An authorized user must review it.");

        if (pending?.State is AgentProvisioningState.PendingReset or AgentProvisioningState.ResetApproved)
        {
            if (pending.PublicKeyHash != publicKeyHash)
                throw new AgentEnrollmentException(409, "This reset is bound to a different public key.");
            pending.DeviceName = name;
            return pending;
        }

        var device = await FindResetDevice(db, name, installationId, ct);
        pending ??= new AgentProvisioningRequest
        {
            Id = Guid.NewGuid(), InstallationId = installationId, DeviceName = name,
            PublicKeyHash = publicKeyHash, RequestedAtUtc = now
        };
        if (db.Entry(pending).State == EntityState.Detached) db.AgentProvisioningRequests.Add(pending);
        pending.DeviceName = name;
        pending.PublicKeyHash = publicKeyHash;
        pending.State = AgentProvisioningState.PendingReset;
        pending.TargetDevice = device;
        pending.AgentCertificate = null;
        pending.ApprovedAtUtc = null;
        pending.ApprovedByUserId = null;
        pending.RequestedAtUtc = now;
        return pending;
    }

    private static async Task<Device?> FindResetDevice(CloneDeployDbContext db, string name, Guid installationId, CancellationToken ct)
    {
        var installation = installationId.ToString("D");
        var matches = await db.Devices.IgnoreQueryFilters()
            .Where(x => x.InstallationId == installation || x.Name.ToLower() == name.ToLower()).ToListAsync(ct);
        if (matches.Count > 1)
            throw new AgentEnrollmentException(409, "Multiple devices match this reset. Administrator recovery is required.");
        var device = matches.SingleOrDefault();
        if (device?.ArchivedAtUtc is not null)
            throw new AgentEnrollmentException(403, "Archived devices cannot request enrollment recovery.");
        return device;
    }

    private static async Task<Device?> FindEnrollmentDevice(CloneDeployDbContext db, string name, Guid installationId,
        AgentProvisioningRequest? pending, string? hardwareUuid, string[]? macAddresses, CancellationToken ct)
    {
        if (hardwareUuid?.Length > 500 || macAddresses?.Length > 64 || macAddresses?.Any(x => x is null || x.Length > 64) == true)
            throw new AgentEnrollmentException(400, "Hardware identity is too large.");
        var uuid = Guid.TryParse(hardwareUuid, out var parsed) && parsed != Guid.Empty &&
            parsed != Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff") ? parsed.ToString("N").ToUpperInvariant() : null;
        var macs = (macAddresses ?? []).Select(x => x.Replace(":", "").Replace("-", "").Trim().ToUpperInvariant())
            .Where(x => x.Length == 12 && x.All(Uri.IsHexDigit) && x != "000000000000" && (Convert.ToByte(x[..2], 16) & 1) == 0)
            .Distinct().ToArray();
        // Imaging registration uses this same table lock, including across server instances.
        await db.Database.ExecuteSqlRawAsync("LOCK TABLE \"Devices\" IN SHARE ROW EXCLUSIVE MODE", ct);
        var matches = await db.Devices.IgnoreQueryFilters().Where(d =>
            (uuid != null && d.UUID != null && d.UUID.Replace("-", "").Replace("{", "").Replace("}", "").Trim().ToUpper() == uuid) ||
            (d.ImagingMac != null && macs.Contains(d.ImagingMac.Replace(":", "").Replace("-", "").Trim().ToUpper())) ||
            d.Nics.Any(n => n.Mac != null && macs.Contains(n.Mac.Replace(":", "").Replace("-", "").Trim().ToUpper())))
            .Take(3).ToListAsync(ct);
        if (matches.Count > 1)
            throw new AgentEnrollmentException(409, "Hardware identifiers match multiple devices. Correct the duplicate hardware records before enrollment.");
        var candidate = matches.SingleOrDefault();
        if (pending?.TargetDeviceId is { } targetId)
        {
            var target = await db.Devices.IgnoreQueryFilters().SingleOrDefaultAsync(d => d.Id == targetId, ct)
                ?? throw new AgentEnrollmentException(409, "The reserved enrollment device is unavailable.");
            if (candidate is not null && candidate.Id != targetId)
                throw new AgentEnrollmentException(409, "Hardware no longer matches the reserved enrollment device.");
            candidate = target;
        }
        if (candidate is null)
        {
            var named = await FindAvailableDevice(db, name, installationId, pending?.Id, ct);
            if (named?.ProvisionStatus == DeviceProvisionStatus.Unmanaged)
                throw new AgentEnrollmentException(409, "The unmanaged device requires matching hardware identifiers before enrollment.");
            return named;
        }
        if (uuid is not null && Guid.TryParse(candidate.UUID, out var stored) && stored != Guid.Empty &&
            stored != Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff") && stored != parsed)
            throw new AgentEnrollmentException(409, "The MAC address belongs to a different hardware UUID.");
        if (await db.Devices.IgnoreQueryFilters().AnyAsync(d => d.Id != candidate.Id && d.Name.ToLower() == name.ToLower(), ct))
            throw new AgentEnrollmentException(409, "The agent hostname belongs to another device. Correct the conflicting record before enrollment.");
        // Keep the existing eligibility, certificate, archive and reservation checks.
        return await FindAvailableDevice(db, candidate.Name, installationId, pending?.Id, ct);
    }

    private static async Task<Device?> FindAvailableDevice(CloneDeployDbContext db, string name, Guid installationId, Guid? requestId, CancellationToken ct)
    {
        var installation = installationId.ToString("D");
        var matches = await db.Devices.IgnoreQueryFilters().Where(x => x.InstallationId == installation || x.Name.ToLower() == name.ToLower()).ToListAsync(ct);
        if (matches.Count > 1) throw new AgentEnrollmentException(409, "Multiple devices match this request. Administrator recovery is required.");
        var device = matches.SingleOrDefault();
        if (device is null) return null;
        if (device.ArchivedAtUtc is not null || !string.IsNullOrEmpty(device.InstallationId) ||
            device.ProvisionStatus is not (DeviceProvisionStatus.NotStarted or DeviceProvisionStatus.PreProvisioned or DeviceProvisionStatus.ProvisionApproved or DeviceProvisionStatus.Unmanaged) ||
            await db.AgentCertificates.AnyAsync(x => x.DeviceId == device.Id, ct) ||
            await db.AgentProvisioningRequests.AnyAsync(x => x.TargetDeviceId == device.Id && x.Id != requestId && x.State != AgentProvisioningState.Denied, ct))
            throw new AgentEnrollmentException(409, "This device is already provisioned, archived or reserved for another request. Administrator recovery is required.");
        return device;
    }
    internal static async Task<string?> ReadProvisioningKeyHashAsync(CloneDeployDbContext db, CancellationToken ct) =>
        (await ReadSettings(db, ct)).KeyHash;

    private static async Task<StoredConfiguration> ReadSettings(CloneDeployDbContext db, CancellationToken ct)
    {
        var json = await db.AppSettings.Where(x => x.Name == SettingsKey).Select(x => x.Value).SingleOrDefaultAsync(ct);
        return json is null ? new(new(), null, 0) : JsonSerializer.Deserialize<StoredConfiguration>(json)
            ?? throw new InvalidOperationException("Invalid provisioning settings.");
    }
    private static async Task WriteSettings(CloneDeployDbContext db, StoredConfiguration settings)
    {
        var entity = await db.AppSettings.SingleOrDefaultAsync(x => x.Name == SettingsKey);
        if (entity is null) { entity = new AppSetting { Name = SettingsKey }; db.AppSettings.Add(entity); }
        entity.Value = JsonSerializer.Serialize(settings);
    }
    private X509Certificate2 LoadIssuer(AppCertificate entity) => X509CertificateLoader.LoadPkcs12(entity.PfxBlob,
        secrets.Unprotect(entity.PasswordEncrypted), X509KeyStorageFlags.EphemeralKeySet);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static Task Lock(CloneDeployDbContext db, CancellationToken ct) => db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(903422)", ct);
    private async Task<int> RequireApprovalPermission(ClaimsPrincipal user)
    {
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.DeviceApprove)).Succeeded ||
            !int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id))
            throw new UnauthorizedAccessException("Manage Approval Requests permission is required.");
        return id;
    }
    private async Task<int> RequireAdmin(ClaimsPrincipal user)
    {
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.AdminOnly)).Succeeded ||
            !int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id)) throw new UnauthorizedAccessException("Administrator access is required.");
        return id;
    }
    private AuditLog Audit(int userId, string? userName, string operation, string name, object details, AuditType type) => new()
    {
        AppUserId = userId, UserName = userName ?? userId.ToString(), AuditType = type,
        ObjectType = operation, ObjectName = name, ObjectJson = JsonSerializer.Serialize(details), LoggedAtUtc = clock.GetUtcNow().UtcDateTime
    };
}
