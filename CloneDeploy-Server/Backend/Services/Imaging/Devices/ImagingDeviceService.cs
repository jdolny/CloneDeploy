using System.Security.Cryptography;
using CloneDeploy_Server.Backend.Common;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed record ImagingHardwareDto(string? Uuid, string? Serial, string? Manufacturer, string? Model, string[]? MacAddresses)
{
    public ImagingNetworkDto[]? Networks { get; init; }
}
public sealed record ImagingNetworkDto(string Mac, string[] IpNetworks);
public sealed record ImagingDeviceRequestDto(ImagingHardwareDto Hardware, string? Name = null);
public sealed record ImagingDeviceDto(int Id, string Name, string LinkToken, bool Created);
public sealed record ImagingFailureLogDto(string DeviceLinkToken, string Contents);

public sealed class ImagingDeviceService(ICloneDeployDbFactory factory, IDataProtectionProvider protection)
{
    private readonly ITimeLimitedDataProtector links = protection.CreateProtector("CloneDeploy.Imaging.DeviceLink.v1").ToTimeLimitedDataProtector();
    private sealed record DeviceLink(int DeviceId, int UserId);

    public async Task SaveFailureLogAsync(ImagingFailureLogDto request, int userId, CancellationToken ct)
    {
        var deviceId = ReadLink(request.DeviceLinkToken, userId);
        if (string.IsNullOrWhiteSpace(request.Contents) || request.Contents.Length > 131072)
            throw new InvalidDataException("Imager log must contain between 1 and 131072 characters.");
        await using var db = await factory.CreateContextAsync();
        if (!await db.Devices.AnyAsync(x => x.Id == deviceId, ct)) throw new KeyNotFoundException();
        db.DeviceImagingLogs.Add(new DeviceImagingLog { DeviceId = deviceId, Contents = request.Contents, Type = "Imager", SubType = "Failed" });
        await db.SaveChangesAsync(ct);
    }

    public int ReadLink(string token, int userId)
    {
        try
        {
            if (token is null || token.Length > 4096) throw new CryptographicException();
            var link = JsonSerializer.Deserialize<DeviceLink>(links.Unprotect(token));
            if (link is null || link.UserId != userId || link.DeviceId <= 0) throw new CryptographicException();
            return link.DeviceId;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        { throw new InvalidDataException("Link this WinPE session to a device before starting capture."); }
    }

    public async Task<ImagingDeviceDto?> ResolveAsync(ImagingDeviceRequestDto request, int userId, CancellationToken ct)
    {
        var hardware = request.Hardware ?? throw new InvalidDataException("Hardware identity is required.");
        if (new[] { hardware.Uuid, hardware.Serial, hardware.Manufacturer, hardware.Model }.Any(x => x?.Length > 500) ||
            hardware.MacAddresses?.Length > 64 || hardware.MacAddresses?.Any(x => x is null || x.Length > 64) == true)
            throw new InvalidDataException("Hardware identity is too large.");
        var uuid = NormalizeUuid(hardware.Uuid);
        var macs = (hardware.MacAddresses ?? []).Select(NormalizeMac).Where(x => x is not null).Cast<string>().Distinct().ToArray();
        if (hardware.Networks is { } networks && (networks.Length > 64 || networks.Any(x => x is null || !macs.Contains(WakeNetwork.Mac(x.Mac))
            || x.IpNetworks is null || x.IpNetworks.Length > 16 || x.IpNetworks.Any(a => a is null || a.Length > 64))))
            throw new InvalidDataException("Invalid network inventory.");
        if (uuid is null && macs.Length == 0) throw new InvalidDataException("No usable hardware UUID or MAC address was found.");
        var serial = hardware.Serial?.Trim() ?? "";
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (request.Name is not null)
        {
            var requestedName = request.Name.Trim();
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended(lower({requestedName}), 81237))", ct);
        }
        // Serialize register/recheck so simultaneous boots of the same hardware cannot create duplicates.
        await db.Database.ExecuteSqlRawAsync("LOCK TABLE \"Devices\" IN SHARE ROW EXCLUSIVE MODE", ct);
        var matches = await db.Devices.Include(d => d.Nics).Where(d =>
                (uuid != null && d.UUID != null && d.UUID.Replace("-", "").Replace("{", "").Replace("}", "").Trim().ToUpper() == uuid) ||
                (d.ImagingMac != null && macs.Contains(d.ImagingMac.Replace(":", "").Replace("-", "").Trim().ToUpper())) ||
                d.Nics.Any(n => n.Mac != null && macs.Contains(n.Mac.Replace(":", "").Replace("-", "").Trim().ToUpper())))
            .Take(3).ToListAsync(ct);
        if (matches.Count > 1) throw new InvalidOperationException("Hardware identifiers match multiple devices. Correct the duplicate UUID/MAC records in CloneDeploy before continuing.");
        var device = matches.SingleOrDefault();
        if (device is not null && (device.ArchivedAtUtc is not null || device.ProvisionStatus == DeviceProvisionStatus.Archived))
            throw new InvalidOperationException("The matching device is archived. Restore it in CloneDeploy before imaging.");
        if (device is not null && uuid is not null && NormalizeUuid(device.UUID) is { } storedUuid && storedUuid != uuid)
            throw new InvalidOperationException("The MAC address belongs to a device with a different hardware UUID. Correct the device record before continuing.");
        var created = false;
        if (device is null)
        {
            if (request.Name is null) return null;
            var name = request.Name.Trim();
            if (CloneDeploy_Agent.Contracts.ComputerNameValidation.Error(name) is { } error)
                throw new InvalidDataException(error);
            if (await db.Devices.AnyAsync(x => x.Name.ToUpper() == name.ToUpper()
                && ((x.ArchivedAtUtc == null && x.ProvisionStatus != DeviceProvisionStatus.Archived)
                    || x.DirectorySourceId != null || x.DirectoryObjectGuid != null), ct))
                throw new InvalidOperationException("That device name is already in use. Choose another name or correct its hardware identifiers.");
            if (!await ImagingPermissions.HasAsync(db, userId, Constants.Permissions.DeviceAdd, ct))
                throw new InvalidOperationException("Add device permission is required to register this computer.");
            device = new Device { Name = name, Guid = Guid.NewGuid(), ProvisionStatus = DeviceProvisionStatus.Unmanaged,
                UUID = uuid is null ? null : Guid.ParseExact(uuid, "N").ToString(), ImagingMac = macs.FirstOrDefault(),
                Bios = new DeviceBios { SerialNumber = serial },
                ComputerSystem = new DeviceComputerSystem { Name = name, Manufacturer = hardware.Manufacturer, Model = hardware.Model },
                Nics = macs.Select(mac => new DeviceNic { Mac = mac, Name = "WinPE adapter" }).ToList() };
            db.Devices.Add(device);
            await db.SaveChangesAsync(ct);
            db.AuditLogs.Add(new AuditLog { AppUserId = userId, UserName = (await db.Users.SingleAsync(x => x.Id == userId, ct)).UserName ?? "Administrator",
                AuditType = AuditType.Create, ObjectType = nameof(Device), ObjectId = device.Id, ObjectName = device.Name,
                ObjectJson = JsonSerializer.Serialize(new { Operation = "WinPeRegistration", device.ProvisionStatus }) });
            await db.SaveChangesAsync(ct);
            created = true;
        }
        // This path requires an authenticated Imager user. Do not trust network inventory from anonymous deployment claims.
        foreach (var network in hardware.Networks ?? [])
        {
            var mac = WakeNetwork.Mac(network.Mac)!;
            var addresses = network.IpNetworks.Where(a => WakeNetwork.TryParse(a, out _, out _, out _)).Distinct().ToArray();
            if (addresses.Length == 0) continue;
            var nic = device.Nics.FirstOrDefault(n => WakeNetwork.Mac(n.Mac) == mac);
            if (nic is null) { nic = new DeviceNic { DeviceId = device.Id, Mac = mac, Name = "WinPE adapter" }; device.Nics.Add(nic); }
            nic.IpNetworks = string.Join(", ", addresses);
            nic.Ips = string.Join(", ", addresses.Select(a => a.Split('/')[0]));
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(device.Id, device.Name, links.Protect(JsonSerializer.Serialize(new DeviceLink(device.Id, userId)), TimeSpan.FromHours(24)), created);
    }

    private static string? NormalizeUuid(string? value) => Guid.TryParse(value, out var guid) && guid != Guid.Empty &&
        guid != Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff") ? guid.ToString("N").ToUpperInvariant() : null;
    private static string? NormalizeMac(string? value)
    {
        var mac = value?.Replace(":", "").Replace("-", "").Trim().ToUpperInvariant();
        return mac is { Length: 12 } && mac.All(Uri.IsHexDigit) && mac != "000000000000" &&
            (Convert.ToByte(mac[..2], 16) & 1) == 0 ? mac : null;
    }
}
