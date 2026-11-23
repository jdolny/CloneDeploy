using System.Text.Json;
using System.Globalization;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Saves inventory information reported by device agents.
/// </summary>
public sealed class AgentInventoryService(ICloneDeployDbFactory factory, TimeProvider clock, NativePxeChanges? nativeChanges = null)
{
    public async Task<bool> SaveAsync(int deviceId, AgentInventory inventory, CancellationToken ct = default)
    {
        InventoryText.RemoveNullCharacters(inventory);
        if (inventory.SchemaVersion != 1 || inventory.ComputerSystem is null || inventory.Os is null
            || inventory.Warnings is null || inventory.Warnings.Count > 30)
            throw new ArgumentException("Inventory requires a supported schema, computer system and operating system.");
        if (inventory.HardDrives?.Count > 4096 || inventory.LogicalVolumes?.Count > 4096
            || inventory.Nics?.Count > 4096 || inventory.Printers?.Count > 4096
            || inventory.Software?.Count > 20000 || inventory.Certificate?.Count > 20000
            || inventory.WindowsUpdate?.Count > 20000)
            throw new ArgumentException("Too many inventory entries.");
        if (inventory.Nics?.Any(n => n.IpNetworks?.Length > 4096) == true)
            throw new ArgumentException("Network inventory is too large.");
        var location = inventory.Os;
        if (location.Latitude.HasValue || location.Longitude.HasValue || location.LastLocationUpdatedAtUtc.HasValue)
        {
            if (location.LocationEnabled != true || location.Latitude is not { } latitude || !double.IsFinite(latitude) || latitude is < -90 or > 90
                || location.Longitude is not { } longitude || !double.IsFinite(longitude) || longitude is < -180 or > 180
                || location.LastLocationUpdatedAtUtc is not { Kind: DateTimeKind.Utc } timestamp || timestamp <= DateTime.UnixEpoch
                || timestamp > clock.GetUtcNow().UtcDateTime.AddMinutes(5))
                throw new ArgumentException("Invalid location inventory.");
        }
        if (inventory.CustomInventory is { } customValidation
            && (customValidation.PolicyRunId == Guid.Empty || customValidation.Results is null
                || customValidation.Results.Count > InventoryCustomValue.MaximumResults
                || customValidation.Results.Any(x => x is null || x.ScriptGuid == Guid.Empty
                    || string.IsNullOrWhiteSpace(x.Value) || x.Value.Length > InventoryCustomValue.MaximumLength)
                || customValidation.Results.Select(x => x.ScriptGuid).Distinct().Count() != customValidation.Results.Count))
            throw new ArgumentException("Invalid custom inventory results.");
        if (inventory.WindowsUpdate?.Any(x => x.RevisionNumber < 0 || x.KBArticleIds?.Length > 128
            || x.KBArticleIds?.Any(id => id is null || id.Length > 64) == true
            || x.Categories?.Count > 128 || x.Categories?.Any(c => c is null
                || string.IsNullOrWhiteSpace(c.Id) || c.Id.Length > 128
                || c.Name is null || c.Name.Length > 512 || c.Type is null || c.Type.Length > 64) == true
            || x.Type is not (null or "Software" or "Driver")) == true)
            throw new ArgumentException("Invalid Windows Update metadata.");
        if (inventory.WindowsUpdateScan is { } scanValidation
            && (scanValidation.CollectedAtUtc == default || scanValidation.CollectedAtUtc.Kind != DateTimeKind.Utc
                || scanValidation.Succeeded != (inventory.WindowsUpdate is not null)))
            throw new ArgumentException("Windows Update scan outcome must match its inventory section.");
        if (inventory.WindowsUpdateScan?.Configuration is { } configuration &&
            (configuration.CollectedAtUtc == default || configuration.CollectedAtUtc.Kind != DateTimeKind.Utc ||
             configuration.TimeZone?.Length > 256 || configuration.Settings is null || configuration.Settings.Count > 256 ||
             configuration.UnavailableSources is null || configuration.UnavailableSources.Count > 16 ||
             configuration.UnavailableSources.Any(x => x is null || x.Length > 1024) ||
             configuration.Settings.Any(x => x is null || string.IsNullOrWhiteSpace(x.Name) || x.Name.Length > 256 ||
                 x.Value is null || x.Value.Length > 8192 || string.IsNullOrWhiteSpace(x.Source) || x.Source.Length > 256 || x.GroupPolicy?.Length > 2048)))
            throw new ArgumentException("Invalid Windows Update configuration snapshot.");
        if (inventory.WindowsUpdateScan?.Configuration?.Activity is { } activity &&
            (activity.CollectedAtUtc == default || activity.CollectedAtUtc.Kind != DateTimeKind.Utc ||
             activity.LastAutomaticInstallationSuccessAtUtc is { Kind: not DateTimeKind.Utc } ||
             activity.LastInstallationSuccessAtUtc is { Kind: not DateTimeKind.Utc } ||
             activity.LastDownloadSuccessAtUtc is { Kind: not DateTimeKind.Utc } ||
             activity.LastFailureAtUtc is { Kind: not DateTimeKind.Utc } ||
             activity.LastFailureOperation is not (null or "Installation" or "Download") ||
             activity.LastFailureErrorCode?.Length > 128 || activity.UnavailableSources is null ||
             activity.UnavailableSources.Count > 8 || activity.UnavailableSources.Any(x => x is null || x.Length > 512)))
            throw new ArgumentException("Invalid Windows Update activity snapshot.");
        await using var db = await factory.CreateContextAsync();
        // Reconcile large sections without repeatedly scanning the entire change tracker.
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Different devices can save concurrently; serialize uploads for the same device.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(682947232, {deviceId})", ct);
        IQueryable<Device> devices = db.Devices;
        if (inventory.ComputerSystem is not null) devices = devices.Include(x => x.ComputerSystem);
        if (inventory.Os is not null) devices = devices.Include(x => x.Os);
        if (inventory.Bios is not null) devices = devices.Include(x => x.Bios);
        if (inventory.Processor is not null) devices = devices.Include(x => x.Processor);
        if (inventory.Gpu is not null) devices = devices.Include(x => x.Gpu);
        if (inventory.AntiVirus is not null) devices = devices.Include(x => x.AntiVirus);
        if (inventory.BitLocker is not null) devices = devices.Include(x => x.BitLocker);
        if (inventory.Firewall is not null) devices = devices.Include(x => x.Firewall);
        var device = await devices.SingleOrDefaultAsync(x => x.Id == deviceId && x.ArchivedAtUtc == null
            && x.ProvisionStatus == DeviceProvisionStatus.Provisioned, ct);
        if (device is null) return false;
        if (inventory.CustomInventory is { } customInventory)
        {
            var run = await db.Set<AgentPolicyRun>().AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == customInventory.PolicyRunId && x.DeviceId == deviceId, ct);
            if (run is null)
                throw new ArgumentException("Custom inventory requires an admitted policy run for this device.");

            var snapshot = JsonSerializer.Deserialize<AgentPolicySnapshot>(run.SnapshotJson)
                ?? throw new ArgumentException("Missing custom inventory policy snapshot.");
            var policy = snapshot.Policy.Deserialize<AgentPolicyContent>()
                ?? throw new ArgumentException("Missing custom inventory policy.");
            if (!policy.Settings.TryGetProperty("RunInventory", out var enabled)
                || !(enabled.ValueKind == JsonValueKind.True
                    || enabled.ValueKind == JsonValueKind.Number && enabled.GetInt32() is >= 1 and <= 3)
                || PolicyUserContext.UsesUserContext(policy.Settings))
                throw new ArgumentException("The policy does not collect device inventory.");

            var allowed = policy.Actions.Where(x => x.Type == (int)ActionType.Script
                && x.Settings.TryGetProperty("AddInventoryCollection", out var flag) && flag.ValueKind == JsonValueKind.True)
                .Select(x => x.Guid).ToHashSet();
            if (customInventory.Results.Any(x => !allowed.Contains(x.ScriptGuid)))
                throw new ArgumentException("A custom inventory script is not assigned to this policy run.");

            var guids = customInventory.Results.Select(x => x.ScriptGuid).ToArray();
            var scripts = await db.ScriptActions.Where(x => guids.Contains(x.Guid))
                .Select(x => new { x.Id, x.Guid }).ToDictionaryAsync(x => x.Guid, ct);
            if (scripts.Count != guids.Length)
                throw new ArgumentException("A custom inventory script no longer exists.");

            var ids = scripts.Values.Select(x => x.Id).ToArray();
            var saved = await db.DeviceCustomInventory.Where(x => x.DeviceId == deviceId && ids.Contains(x.ScriptActionId))
                .ToListAsync(ct);
            foreach (var result in customInventory.Results)
            {
                var id = scripts[result.ScriptGuid].Id;
                var entry = saved.FirstOrDefault(x => x.ScriptActionId == id);
                if (entry is null)
                    db.DeviceCustomInventory.Add(new DeviceCustomInventory { DeviceId = deviceId, ScriptActionId = id, Value = result.Value });
                else
                    entry.Value = result.Value;
            }
        }
        var nativeChanged = false;
        if (inventory.ComputerSystem is { } computerSystem)
        {
            var entity = device.ComputerSystem;
            nativeChanged = entity?.Manufacturer != computerSystem.Manufacturer || entity?.Model != computerSystem.Model;
            if (entity is null)
            {
                entity = new DeviceComputerSystem { DeviceId = deviceId };
                db.Add(entity);
            }
            entity.Manufacturer = computerSystem.Manufacturer;
            entity.Model = computerSystem.Model;
            entity.Name = computerSystem.Name;
            entity.Domain = computerSystem.Domain;
            entity.Workgroup = computerSystem.Workgroup;
            entity.Memory = computerSystem.Memory;
        }
        if (inventory.Os is { } os)
        {
            var entity = device.Os;
            if (entity is null)
            {
                entity = new DeviceOs { DeviceId = deviceId };
                db.Add(entity);
            }
            entity.Caption = os.Caption;
            entity.Version = os.Version;
            entity.BuildNumber = os.BuildNumber;
            entity.OSArchitecture = os.OSArchitecture;
            entity.NativeArchitecture = os.NativeArchitecture;
            entity.ReleaseId = os.ReleaseId;
            entity.UacStatus = os.UacStatus;
            entity.LocalTimeZone = os.LocalTimeZone;
            entity.UpdateServer = os.UpdateServer;
            entity.SUStargetGroup = os.SUStargetGroup;
            if (os.LocationEnabled is { } enabled) entity.LocationEnabled = enabled;
            // An unavailable or older reading must not erase the last known location.
            if (os.LastLocationUpdatedAtUtc is { } updated && updated > entity.LastLocationUpdatedAtUtc)
            {
                entity.Latitude = os.Latitude!.Value.ToString("R", CultureInfo.InvariantCulture);
                entity.Longitude = os.Longitude!.Value.ToString("R", CultureInfo.InvariantCulture);
                entity.LastLocationUpdatedAtUtc = updated;
            }
        }
        if (inventory.Bios is { } bios)
        {
            var entity = device.Bios;
            if (entity is null)
            {
                entity = new DeviceBios { DeviceId = deviceId };
                db.Add(entity);
            }
            entity.SerialNumber = bios.SerialNumber;
            entity.Version = bios.Version;
            entity.SMBIOSBIOSVersion = bios.SMBIOSBIOSVersion;
        }
        if (inventory.Processor is { } processor)
        {
            var entity = device.Processor;
            if (entity is null)
            {
                entity = new DeviceProcessor { DeviceId = deviceId };
                db.Add(entity);
            }
            entity.Name = processor.Name;
            entity.Speed = processor.Speed;
            entity.Cores = processor.Cores;
        }
        if (inventory.Gpu is { } gpu)
        {
            var entity = device.Gpu;
            if (entity is null)
            {
                entity = new DeviceGpu { DeviceId = deviceId };
                db.Add(entity);
            }
            entity.Name = gpu.Name;
            entity.Memory = gpu.Memory;
        }
        if (inventory.AntiVirus is { } antiVirus)
        {
            var entity = device.AntiVirus;
            if (entity is null)
            {
                entity = new DeviceAntiVirus { DeviceId = deviceId };
                db.Add(entity);
            }
            entity.DisplayName = antiVirus.DisplayName;
            entity.Provider = antiVirus.Provider;
            entity.RealtimeScanner = antiVirus.RealtimeScanner;
            entity.DefinitionStatus = antiVirus.DefinitionStatus;
            entity.ProductState = antiVirus.ProductState;
        }
        if (inventory.BitLocker is { } bitLocker)
        {
            var entity = device.BitLocker;
            if (entity is null)
            {
                entity = new DeviceBitLocker { DeviceId = deviceId };
                db.Add(entity);
            }
            entity.DriveLetter = bitLocker.DriveLetter;
            entity.Status = bitLocker.Status;
        }
        if (inventory.Firewall is { } firewall)
        {
            var entity = device.Firewall;
            if (entity is null)
            {
                entity = new DeviceFirewall { DeviceId = deviceId };
                db.Add(entity);
            }
            entity.DomainEnabled = firewall.DomainEnabled;
            entity.PrivateEnabled = firewall.PrivateEnabled;
            entity.PublicEnabled = firewall.PublicEnabled;
        }
        if (inventory.HardDrives is { } hardDrives)
        {
            SyncRows(db, await db.Set<DeviceHardDrive>().Where(x => x.DeviceId == deviceId).ToListAsync(ct),
                hardDrives.Select(x => new DeviceHardDrive
            {
                DeviceId = deviceId,
                Model = x.Model,
                FirmwareRevision = x.FirmwareRevision,
                SerialNumber = x.SerialNumber,
                SizeMb = x.SizeMb,
                Status = x.Status,
            }), x => (x.SerialNumber, x.Model));
        }
        if (inventory.LogicalVolumes is { } logicalVolumes)
        {
            SyncRows(db, await db.Set<DeviceLogicalVolume>().Where(x => x.DeviceId == deviceId).ToListAsync(ct),
                logicalVolumes.Select(x => new DeviceLogicalVolume
            {
                DeviceId = deviceId,
                Drive = x.Drive,
                FreeSpaceGB = x.FreeSpaceGB,
                FreeSpacePercent = x.FreeSpacePercent,
                SizeGB = x.SizeGB,
            }), x => x.Drive);
        }
        if (inventory.Nics is { } nics)
        {
            var existingNics = await db.Set<DeviceNic>().Where(x => x.DeviceId == deviceId).ToListAsync(ct);
            nativeChanged |= !existingNics.Select(x => NativePxeReservations.Normalize(x.Mac)).Where(x => x != null).ToHashSet()
                .SetEquals(nics.Select(x => NativePxeReservations.Normalize(x.Mac)).Where(x => x != null));
            SyncRows(db, existingNics,
                nics.Select(x => new DeviceNic
            {
                DeviceId = deviceId,
                Name = x.Name,
                Description = x.Description,
                Type = x.Type,
                Mac = x.Mac,
                Status = x.Status,
                Speed = x.Speed,
                Ips = x.Ips,
                IpNetworks = x.IpNetworks,
                Gateways = x.Gateways,
            }), x => (x.Mac, x.Name));
        }
        if (inventory.Printers is { } printers)
        {
            SyncRows(db, await db.Set<DevicePrinter>().Where(x => x.DeviceId == deviceId).ToListAsync(ct),
                printers.Select(x => new DevicePrinter
            {
                DeviceId = deviceId,
                Name = x.Name,
                DriverName = x.DriverName,
                Local = x.Local,
                Network = x.Network,
                ShareName = x.ShareName,
                SystemName = x.SystemName,
            }), x => (x.Name, x.SystemName));
        }
        if (inventory.Software is { } software)
        {
            var names = software.Select(x => x.Name).Distinct().ToList();
            var catalog = await GetCatalogAsync(db,
                () => db.Software.Where(x => names.Contains(x.Name)).OrderBy(x => x.Id).ToListAsync(ct),
                software.Select(x => new Software { Name = x.Name, Version = x.Version,
                    Major = x.Major, Minor = x.Minor, Build = x.Build, Revision = x.Revision }),
                x => (x.Name, x.Version), json => db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "Software" ("Name", "Version", "Major", "Minor", "Build", "Revision")
                    SELECT "Name", "Version", "Major", "Minor", "Build", "Revision"
                    FROM jsonb_to_recordset({json}::jsonb) AS incoming(
                        "Name" text, "Version" text, "Major" integer, "Minor" integer, "Build" integer, "Revision" integer)
                    ORDER BY "Name" COLLATE "C" NULLS FIRST, "Version" COLLATE "C" NULLS FIRST
                    ON CONFLICT ("Name", "Version") DO NOTHING;
                    """, ct), ct);
            SyncRows(db, await db.DeviceSoftware.Where(x => x.DeviceId == deviceId).Include(x => x.Software).ToListAsync(ct),
                software.DistinctBy(x => (x.Name, x.Version, x.UninstallString)).Select(x => new DeviceSoftware
                {
                    DeviceId = deviceId, Software = catalog[(x.Name, x.Version)],
                    SoftwareId = catalog[(x.Name, x.Version)].Id, UninstallString = x.UninstallString
                }), x => (x.Software!.Name, x.Software.Version), x => x.UninstallString);
        }
        if (inventory.Certificate is { } certificates)
        {
            var thumbprints = certificates.Select(x => x.Thumbprint).Distinct().ToList();
            var catalog = await GetCatalogAsync(db,
                () => db.Certificates.Where(x => thumbprints.Contains(x.Thumbprint)).OrderBy(x => x.Id).ToListAsync(ct),
                certificates.Select(x => new Certificate { Store = x.Store, Subject = x.Subject,
                    FriendlyName = x.FriendlyName, Thumbprint = x.Thumbprint, Serial = x.Serial, Issuer = x.Issuer,
                    NotBeforeUtc = x.NotBeforeUtc, NotAfterUtc = x.NotAfterUtc }),
                x => (x.Store, x.Thumbprint), json => db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "Certificates" ("Store", "Subject", "FriendlyName", "Thumbprint", "Serial", "Issuer", "NotBeforeUtc", "NotAfterUtc")
                    SELECT "Store", "Subject", "FriendlyName", "Thumbprint", "Serial", "Issuer", "NotBeforeUtc", "NotAfterUtc"
                    FROM jsonb_to_recordset({json}::jsonb) AS incoming(
                        "Store" text, "Subject" text, "FriendlyName" text, "Thumbprint" text, "Serial" text, "Issuer" text,
                        "NotBeforeUtc" timestamp with time zone, "NotAfterUtc" timestamp with time zone)
                    ORDER BY "Thumbprint" COLLATE "C", "Store" COLLATE "C"
                    ON CONFLICT ("Thumbprint", "Store") DO NOTHING;
                    """, ct), ct);
            SyncRows(db, await db.DeviceCertificates.Where(x => x.DeviceId == deviceId).Include(x => x.Certificate).ToListAsync(ct),
                certificates.DistinctBy(x => (x.Store, x.Thumbprint)).Select(x => new DeviceCertificate
                {
                    DeviceId = deviceId, Certificate = catalog[(x.Store, x.Thumbprint)],
                    CertificateId = catalog[(x.Store, x.Thumbprint)].Id
                }), x => (x.Certificate!.Store, x.Certificate.Thumbprint));
        }
        if (inventory.WindowsUpdate is { } updates)
        {
            var observed = updates.GroupBy(x => x.UpdateId).Select(x => x.OrderByDescending(u => u.RevisionNumber).First()).ToList();
            var ids = observed.Select(x => x.UpdateId).ToList();
            var entries = await db.WindowsUpdates.Where(x => ids.Contains(x.UpdateId)).ToListAsync(ct);
            var catalog = entries.ToDictionary(x => ValueTuple.Create(x.UpdateId));
            var changed = observed.Where(x => !catalog.TryGetValue(ValueTuple.Create(x.UpdateId), out var entry)
                || x.RevisionNumber is not null && (entry.RevisionNumber is null || x.RevisionNumber > entry.RevisionNumber))
                .Select(x => new WindowsUpdate
                {
                    UpdateId = x.UpdateId, Title = x.Title, Category = x.Category, RevisionNumber = x.RevisionNumber,
                    KBArticleIds = x.KBArticleIds, CategoriesJson = x.Categories is null ? null : JsonSerializer.Serialize(x.Categories),
                    MsrcSeverity = x.MsrcSeverity, Type = x.Type, RevisionFirstSeenAtUtc = clock.GetUtcNow().UtcDateTime
                }).ToList();
            if (changed.Count > 0)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "WindowsUpdates" AS catalog
                        ("UpdateId", "Title", "Category", "RevisionNumber", "KBArticleIds", "CategoriesJson", "MsrcSeverity", "Type", "RevisionFirstSeenAtUtc")
                    SELECT "UpdateId", "Title", "Category", "RevisionNumber", "KBArticleIds", "CategoriesJson"::jsonb, "MsrcSeverity", "Type", "RevisionFirstSeenAtUtc"
                    FROM jsonb_to_recordset({JsonSerializer.Serialize(changed)}::jsonb) AS incoming(
                        "UpdateId" text, "Title" text, "Category" text, "RevisionNumber" integer, "KBArticleIds" text[],
                        "CategoriesJson" text, "MsrcSeverity" text, "Type" text, "RevisionFirstSeenAtUtc" timestamptz)
                    ORDER BY "UpdateId" COLLATE "C" NULLS FIRST
                    ON CONFLICT ("UpdateId") DO UPDATE SET
                        "Title" = EXCLUDED."Title", "Category" = EXCLUDED."Category",
                        "RevisionNumber" = EXCLUDED."RevisionNumber", "KBArticleIds" = EXCLUDED."KBArticleIds",
                        "CategoriesJson" = EXCLUDED."CategoriesJson", "MsrcSeverity" = EXCLUDED."MsrcSeverity", "Type" = EXCLUDED."Type",
                        "RevisionFirstSeenAtUtc" = EXCLUDED."RevisionFirstSeenAtUtc"
                    WHERE EXCLUDED."RevisionNumber" IS NOT NULL
                        AND (catalog."RevisionNumber" IS NULL OR EXCLUDED."RevisionNumber" > catalog."RevisionNumber");
                    """, ct);
                // Raw SQL can update existing rows; remove their stale tracked snapshots before reloading.
                foreach (var entry in entries) db.Entry(entry).State = EntityState.Detached;
                catalog = (await db.WindowsUpdates.Where(x => ids.Contains(x.UpdateId)).ToListAsync(ct))
                    .ToDictionary(x => ValueTuple.Create(x.UpdateId));
            }
            SyncRows(db, await db.DeviceWindowsUpdates.Where(x => x.DeviceId == deviceId).Include(x => x.WindowsUpdate).ToListAsync(ct),
                observed.Select(x => new DeviceWindowsUpdate
                {
                    DeviceId = deviceId, WindowsUpdate = catalog[ValueTuple.Create(x.UpdateId)],
                    WindowsUpdateId = catalog[ValueTuple.Create(x.UpdateId)].Id,
                    IsInstalled = x.IsInstalled, LastDeploymentChangedAtUtc = x.LastDeploymentChangedAtUtc,
                    RevisionNumber = x.RevisionNumber, IsDownloaded = x.IsDownloaded, IsHidden = x.IsHidden, BrowseOnly = x.BrowseOnly
                }), x => ValueTuple.Create(x.WindowsUpdate!.UpdateId));
        }
        if (inventory.WindowsUpdateScan is { } scan)
        {
            var entity = await db.Set<DeviceWindowsUpdateScan>().SingleOrDefaultAsync(x => x.DeviceId == deviceId, ct);
            if (entity is null)
            {
                entity = new DeviceWindowsUpdateScan { DeviceId = deviceId };
                db.Add(entity);
            }
            if (entity.SourceId != scan.SourceId) entity.LastSuccessfulOnlineScanAtUtc = null;
            entity.CollectedAtUtc = scan.CollectedAtUtc;
            entity.IsOnline = scan.IsOnline;
            entity.Succeeded = scan.Succeeded;
            entity.ResultCode = scan.ResultCode;
            entity.ErrorCode = scan.ErrorCode;
            entity.SourceName = scan.SourceName;
            entity.SourceId = scan.SourceId;
            entity.LastSuccessfulSearchAtUtc = scan.LastSuccessfulSearchAtUtc;
            entity.RebootRequired = scan.RebootRequired;
            // Older agents omit configuration; retain the previous snapshot and its own timestamp.
            if (scan.Configuration is not null)
                entity.ConfigurationJson = JsonSerializer.Serialize(scan.Configuration);
            if (scan.IsOnline && scan.Succeeded) entity.LastSuccessfulOnlineScanAtUtc = scan.CollectedAtUtc;
        }
        if (Guid.TryParse(inventory.HardwareUuid, out var uuid) && uuid != Guid.Empty &&
            uuid != Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff")) device.UUID = uuid.ToString();
        device.LastInventoryAtUtc = clock.GetUtcNow().UtcDateTime;
        device.LastSocketResult = inventory.Warnings.Count == 0 ? "Inventory saved." : "Inventory saved. Unavailable sections: " + string.Join(", ", inventory.Warnings);
        db.ChangeTracker.DetectChanges();
        // Keep the shared registration lock limited to persistence and reconciliation.
        await db.Database.ExecuteSqlRawAsync("LOCK TABLE \"Devices\" IN SHARE ROW EXCLUSIVE MODE", ct);
        await db.SaveChangesAsync(ct);
        var reconciliation = await DeviceHardwareReconciliation.ReconcileAsync(db, device, inventory, ct);
        if (reconciliation.Message is not null)
        {
            device.LastSocketResult += " " + reconciliation.Message;
            nativeChanged = true;
            db.ChangeTracker.DetectChanges();
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        if (reconciliation.RemovedDeviceId is { } removedId) nativeChanges?.DeviceChanged(removedId);
        if (nativeChanged) nativeChanges?.DeviceChanged(deviceId);
        return true;
    }

    private static async Task<Dictionary<TKey, TEntity>> GetCatalogAsync<TEntity, TKey>(
        CloneDeployDbContext db, Func<Task<List<TEntity>>> load, IEnumerable<TEntity> incoming,
        Func<TEntity, TKey> key, Func<string, Task> insertMissing, CancellationToken ct) where TEntity : class where TKey : notnull
    {
        var requested = incoming.DistinctBy(key).ToList();
        if (requested.Count == 0) return [];
        var catalog = (await load()).ToDictionary(key);
        var missing = requested.Where(x => !catalog.ContainsKey(key(x))).ToList();
        if (missing.Count == 0) return catalog;

        // Batch only missing entries. A fixed catalog/key insertion order avoids deadlocks
        // between overlapping uploads; uniqueness handles competing inserts without a shared lock.
        ct.ThrowIfCancellationRequested();
        await insertMissing(JsonSerializer.Serialize(missing));
        // Use a separate statement so Read Committed sees rows committed by competing inserts.
        return (await load()).ToDictionary(key);
    }

    private static void SyncRows<TEntity, TKey>(CloneDeployDbContext db, List<TEntity> existing,
        IEnumerable<TEntity> incoming, Func<TEntity, TKey> key, Func<TEntity, string?>? detail = null)
        where TEntity : class where TKey : notnull
    {
        detail ??= _ => null;
        var remaining = existing.ToHashSet();
        var exact = existing.GroupBy(x => (key(x), detail(x)))
            .ToDictionary(x => x.Key, x => new Queue<TEntity>(x));
        var pending = new List<TEntity>();
        // Reserve exact matches first, including different installations of the same software,
        // so a changed command cannot consume another installation's unchanged record.
        foreach (var item in incoming)
        {
            if (exact.TryGetValue((key(item), detail(item)), out var matches) && matches.TryDequeue(out var match))
            {
                CopyValues(match, item);
                remaining.Remove(match);
            }
            else pending.Add(item);
        }
        var byKey = remaining.GroupBy(key).ToDictionary(x => x.Key, x => new Queue<TEntity>(x));
        foreach (var item in pending)
        {
            if (byKey.TryGetValue(key(item), out var matches) && matches.TryDequeue(out var match))
            {
                CopyValues(match, item);
                remaining.Remove(match);
            }
            else db.Add(item);
        }
        db.RemoveRange(remaining);

        void CopyValues(TEntity target, TEntity source)
        {
            var values = db.Entry(target).CurrentValues;
            var incomingValues = db.Entry(source).CurrentValues;
            foreach (var property in values.Properties)
                if (!property.IsPrimaryKey()) values[property] = incomingValues[property];
        }
    }
}
