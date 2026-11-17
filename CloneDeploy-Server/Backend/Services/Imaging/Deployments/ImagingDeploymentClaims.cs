using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed record ImagingDeploymentClaimDto(ImagingHardwareDto Hardware, string ClaimKey);
public sealed record ImagingDeploymentAssignmentDto(Guid DeploymentId, int DeviceId, string DeviceName,
    Guid ImageVersionId, string ImageName, string ProfileName, DateTime CreatedAtUtc)
{
    public bool AgentInitiated { get; init; }
}
public sealed record ImagingDeploymentSessionDto(string TokenType, string AccessToken, int ExpiresIn,
    ImagingDeploymentAssignmentDto Assignment);

/// <summary>Unattended access to a preassigned deployment only. Hardware identifiers are not proof of identity.</summary>
public sealed partial class ImagingDeploymentClaims(ICloneDeployDbFactory factory, IDataProtectionProvider protection,
    StorageReadSelection? readSelection = null, IHttpContextAccessor? requests = null)
{
    public SiteDownloadDelivery? Site { get; init; }
    private const int LifetimeSeconds = 15 * 60;
    private readonly ITimeLimitedDataProtector _tokens = protection.CreateProtector("CloneDeploy.Imaging.DeploymentClaim.v1").ToTimeLimitedDataProtector();
    private sealed record Ticket(Guid DeploymentId, int DeviceId, Guid ClaimId);
    private const string Prefix = "cdi_deploy_";

    public async Task<ImagingDeploymentSessionDto?> ClaimAsync(ImagingDeploymentClaimDto request, CancellationToken ct)
    {
        if (request.ClaimKey is not { Length: 64 } || !request.ClaimKey.All(Uri.IsHexDigit))
            throw new ArgumentException("A random 32-byte claim key is required.");
        var hardware = request.Hardware ?? throw new ArgumentException("Hardware identity is required.");
        if (new[] { hardware.Uuid, hardware.Serial, hardware.Manufacturer, hardware.Model }.Any(x => x?.Length > 500) ||
            hardware.MacAddresses?.Length > 64 || hardware.MacAddresses?.Any(x => x is null || x.Length > 64) == true)
            throw new ArgumentException("Hardware identity is too large.");
        var uuid = Uuid(hardware.Uuid);
        var macs = (hardware.MacAddresses ?? []).Select(NativePxeReservations.Normalize).Where(x => x != null).Cast<string>().Distinct().ToArray();
        if (uuid is null && macs.Length == 0) throw new ArgumentException("No usable hardware UUID or MAC address was found.");
        var serial = hardware.Serial?.Trim();
        await using var db = await factory.CreateContextAsync();
        var matches = await db.Devices.AsNoTracking().Where(d =>
            (uuid != null && d.UUID != null && d.UUID.Replace("-", "").Replace("{", "").Replace("}", "").Trim().ToUpper() == uuid) ||
            (d.ImagingMac != null && macs.Contains(d.ImagingMac.Replace(":", "").Replace("-", "").Replace(".", "").Trim().ToUpper())) ||
            d.Nics.Any(n => n.Mac != null && macs.Contains(n.Mac.Replace(":", "").Replace("-", "").Replace(".", "").Trim().ToUpper())))
            .Select(d => new { d.Id, d.UUID, Serial = d.Bios == null ? null : d.Bios.SerialNumber, d.ArchivedAtUtc, d.ProvisionStatus })
            .Take(3).ToListAsync(ct);
        if (matches.Count > 1) throw new InvalidOperationException("Hardware identifiers match multiple devices. Correct the device records before continuing.");
        var device = matches.SingleOrDefault();
        if (device is null) return null;
        if (device.ArchivedAtUtc != null || device.ProvisionStatus == DeviceProvisionStatus.Archived)
            throw new InvalidOperationException("This device is archived.");
        if (uuid != null && Uuid(device.UUID) is { } storedUuid && uuid != storedUuid ||
            !string.IsNullOrWhiteSpace(serial) && !string.IsNullOrWhiteSpace(device.Serial) && !string.Equals(serial, device.Serial.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Hardware identifiers conflict with the stored device. Correct the record before continuing.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Same lock as admin preparation/cancellation/completion; competing claimants serialize.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(682947233, {device.Id})", ct);
        var deployment = await Pending(db).SingleOrDefaultAsync(x => x.DeviceId == device.Id, ct);
        if (deployment is null) return null;
        if (await MulticastAsync(db, deployment, ct) is { } multicast && (multicast.State != MulticastState.Waiting || multicast.StartRequested))
            throw new InvalidOperationException("This multicast session has already started. Create a new session for missed devices.");
        var hash = Convert.ToHexString(SHA256.HashData(Convert.FromHexString(request.ClaimKey)));
        var now = DateTime.UtcNow;
        var existing = deployment.ImagerClaimId != null && deployment.ImagerClaimExpiresAtUtc > now;
        if (deployment.ImagerStartedAtUtc != null || deployment.ImagerFailedAtUtc != null)
            throw new InvalidOperationException("This deployment has already started. Cancel it and prepare a new deployment before retrying.");
        if (existing && deployment.ImagerClaimKeyHash != hash)
            throw new InvalidOperationException("This deployment is claimed by another Imager session. Close that session or wait for its lease to expire.");
        var assignment = await AssignmentAsync(db, deployment, ct);
        await ResolveDriversAsync(db, deployment, hardware, ct);
        if (!existing)
        {
            deployment.ImagerClaimId = Guid.NewGuid();
            deployment.ImagerClaimKeyHash = hash;
        }
        deployment.ImagerClaimExpiresAtUtc = now.AddSeconds(LifetimeSeconds);
        await DeploymentTaskTracking.SyncAsync(db, deployment, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        var ticket = new Ticket(deployment.Id, device.Id, deployment.ImagerClaimId!.Value);
        return new("Bearer", Prefix + _tokens.Protect(JsonSerializer.Serialize(ticket), TimeSpan.FromSeconds(LifetimeSeconds)), LifetimeSeconds, assignment);
    }

    public async Task<ImagingDeploymentAssignmentDto?> ReadAsync(string? token, CancellationToken ct)
    {
        var ticket = ReadTicket(token);
        if (ticket is null) return null;
        await using var db = await factory.CreateContextAsync();
        var deployment = await Pending(db).AsNoTracking().SingleOrDefaultAsync(x => x.Id == ticket.DeploymentId && x.DeviceId == ticket.DeviceId &&
            x.ImagerClaimId == ticket.ClaimId && x.ImagerClaimExpiresAtUtc > DateTime.UtcNow, ct);
        return deployment is null ? null : await AssignmentAsync(db, deployment, ct);
    }
    public async Task<bool> ReleaseAsync(string? token, CancellationToken ct)
    {
        var ticket = ReadTicket(token);
        if (ticket is null) return false;
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(682947233, {ticket.DeviceId})", ct);
        // Compare-and-clear cannot release a newer claimant's lease.
        var released = await Pending(db).Where(x => x.Id == ticket.DeploymentId && x.DeviceId == ticket.DeviceId &&
                x.ImagerClaimId == ticket.ClaimId && x.ImagerClaimExpiresAtUtc > DateTime.UtcNow)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ImagerClaimId, (Guid?)null)
                .SetProperty(x => x.ImagerClaimKeyHash, (string?)null).SetProperty(x => x.ImagerClaimExpiresAtUtc, (DateTime?)null), ct) == 1;
        if (released)
        {
            var deployment = await db.Set<ImagingDeployment>().SingleAsync(x => x.Id == ticket.DeploymentId, ct);
            await DeploymentTaskTracking.SyncAsync(db, deployment, ct);
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return released;
    }
    private Ticket? ReadTicket(string? token)
    {
        if (token is null || token.Length > 4096 || !token.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        try { return JsonSerializer.Deserialize<Ticket>(_tokens.Unprotect(token[Prefix.Length..])); }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException) { return null; }
    }
    private static IQueryable<ImagingDeployment> Pending(CloneDeployDbContext db) => db.Set<ImagingDeployment>().Where(x =>
        (x.AgentBootPlanJson == null || x.AgentBootReady) && x.CancelledAtUtc == null && x.ImagingCompletedAtUtc == null && x.SetupCompletedAtUtc == null &&
        db.Devices.Any(d => d.Id == x.DeviceId && d.ArchivedAtUtc == null && d.ProvisionStatus != DeviceProvisionStatus.Archived));
    private static async Task<ImagingDeploymentAssignmentDto> AssignmentAsync(CloneDeployDbContext db, ImagingDeployment deployment, CancellationToken ct)
    {
        var settings = JsonSerializer.Deserialize<DeploymentSettingsDto>(deployment.SettingsJson)
            ?? throw new InvalidOperationException("The assigned deployment has invalid settings.");
        var deviceName = await db.Devices.Where(x => x.Id == deployment.DeviceId).Select(x => x.Name).SingleAsync(ct);
        var imageName = await db.ImageVersions.Where(x => x.Id == deployment.ImageVersionId).Select(x => x.Name).SingleAsync(ct);
        return new(deployment.Id, deployment.DeviceId, deviceName, deployment.ImageVersionId, imageName, settings.ProfileName, deployment.CreatedAtUtc)
        { AgentInitiated = deployment.AgentBootPlanJson is not null };
    }
    private static string? Uuid(string? value) => Guid.TryParse(value, out var id) && id != Guid.Empty &&
        id != Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff") ? id.ToString("N").ToUpperInvariant() : null;
}
