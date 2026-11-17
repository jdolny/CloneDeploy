using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;
using CloneDeploy_Server.Backend.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Manages device records, custom field values, archiving, and deletion.
/// </summary>
public sealed class DeviceAdministration(
    ICloneDeployDbFactory dbFactory,
    AuthenticationStateProvider authenticationStateProvider,
    IAuthorizationService authorization)
{
    private readonly ICloneDeployDbFactory _dbFactory = dbFactory;

    public async Task<ServiceResult<Device>> GetByIdAsync(int id)
    {
        if (!await CanDeviceAsync(Constants.Permissions.DeviceView)) return ServiceResult<Device>.Fail("Device.Forbidden");
        await using var db = await _dbFactory.CreateContextAsync();
        var device = await db.Devices.AsNoTracking().AsSplitQuery()
            .Include(x => x.DirectoryObject).ThenInclude(x => x!.DirectorySource)
            .Include(x => x.Tags).ThenInclude(x => x.Tag)
            .Include(x => x.Groups).ThenInclude(x => x.DeviceGroup)
            .Include(x => x.Attachments).ThenInclude(x => x.Attachment)
            .Include(x => x.Certificates).ThenInclude(x => x.Certificate)
            .Include(x => x.Comment).ThenInclude(x => x.Comment)
            .Include(x => x.CustomAttributes).ThenInclude(x => x.CustomAttribute)
            .Include(x => x.CustomInventories).ThenInclude(x => x.ScriptAction)
            .Include(x => x.ImagingLogs)
            .Include(x => x.Softwares).ThenInclude(x => x.Software)
            .Include(x => x.ComputerSystem).Include(x => x.Os).Include(x => x.Bios)
            .Include(x => x.Processor).Include(x => x.Gpu).Include(x => x.Nics)
            .Include(x => x.HardDrives).Include(x => x.LogicalVolumes).Include(x => x.Printers)
            .Include(x => x.AntiVirus).Include(x => x.Firewall).Include(x => x.BitLocker)
            .Include(x => x.Logins)
            .Include(x => x.WindowsUpdateScan).Include(x => x.WindowsUpdates).ThenInclude(x => x.WindowsUpdate)
            .SingleOrDefaultAsync(x => x.Id == id);
        if (device is not null) await AddBuiltInGroupsAsync(db, device);
        return device is null
            ? ServiceResult<Device>.Fail(CloneDeploy_Server.Localization.ErrorKeys.Common.NotFound, id)
            : ServiceResult<Device>.Ok(device);
    }

    public async Task<ServiceResult<Device>> GetDetailsAsync(int id, string tab)
    {
        if (!await CanDeviceAsync(Constants.Permissions.DeviceView)) return ServiceResult<Device>.Fail("Device.Forbidden");
        await using var db = await _dbFactory.CreateContextAsync();
        IQueryable<Device> query = db.Devices.AsNoTracking().AsSplitQuery();
        query = tab switch
        {
            "general" => query.Include(x => x.DirectoryObject).ThenInclude(x => x!.DirectorySource)
                .Include(x => x.Tags).ThenInclude(x => x.Tag)
                .Include(x => x.Groups).ThenInclude(x => x.DeviceGroup),
            "relations" => query.Include(x => x.Groups).ThenInclude(x => x.DeviceGroup)
                .Include(x => x.Tags).ThenInclude(x => x.Tag)
                .Include(x => x.DirectoryObject).ThenInclude(x => x!.DirectorySource)
                .Include(x => x.Image).Include(x => x.DeploymentProfile),
            "attachments" => query.Include(x => x.Attachments).ThenInclude(x => x.Attachment),
            "certificates" => query.Include(x => x.Certificates).ThenInclude(x => x.Certificate),
            "comments" => query.Include(x => x.Comment).ThenInclude(x => x.Comment),
            "custom-attributes" => query.Include(x => x.CustomAttributes).ThenInclude(x => x.CustomAttribute),
            "custom-inventory" => query.Include(x => x.CustomInventories).ThenInclude(x => x.ScriptAction),
            "imaging-logs" => query.Include(x => x.ImagingLogs),
            "software" => query.Include(x => x.Softwares).ThenInclude(x => x.Software),
            "system-info" => query.Include(x => x.ComputerSystem).Include(x => x.Os).Include(x => x.Bios)
                .Include(x => x.WindowsUpdateScan)
                .Include(x => x.Processor).Include(x => x.Gpu).Include(x => x.Nics)
                .Include(x => x.HardDrives).Include(x => x.LogicalVolumes).Include(x => x.Printers)
                .Include(x => x.AntiVirus).Include(x => x.Firewall).Include(x => x.BitLocker),
            "user-logins" => query.Include(x => x.Logins),
            "windows-updates" => query.Include(x => x.WindowsUpdateScan).Include(x => x.WindowsUpdates).ThenInclude(x => x.WindowsUpdate),
            _ => query
        };
        var device = await query.SingleOrDefaultAsync(x => x.Id == id);
        if (device is not null && tab is "general" or "relations") await AddBuiltInGroupsAsync(db, device);
        return device is null
            ? ServiceResult<Device>.Fail(CloneDeploy_Server.Localization.ErrorKeys.Common.NotFound, id)
            : ServiceResult<Device>.Ok(device);
    }

    // These are detached display relations only; virtual membership is never saved.
    private static async Task AddBuiltInGroupsAsync(CloneDeployDbContext db, Device device)
    {
        var groups = await DeviceGroupMembershipQuery.ForDevice(db, device.Id).AsNoTracking()
            .Where(g => g.Type == DeviceGroupType.AllDevices).ToListAsync();
        foreach (var group in groups)
            if (!device.Groups.Any(m => m.DeviceGroupId == group.Id))
                device.Groups.Add(new DeviceGroupMembership { DeviceId = device.Id, DeviceGroupId = group.Id, DeviceGroup = group });
    }

    public async Task<ServiceResult<List<Device>>> GetDevicesAsync(DeviceFilterDto? deviceFilter = null)
    {
        if (!await CanDeviceAsync(Constants.Permissions.DeviceView)) return ServiceResult<List<Device>>.Fail("Device.Forbidden");
        await using var db = await _dbFactory.CreateContextAsync();
        return ServiceResult<List<Device>>.Ok(await db.Devices.AsNoTracking().ToListAsync());
    }

    public async Task<ServiceResult> ArchiveDeviceAsync(int id)
    {
        if (!await CanDeviceAsync(Constants.Permissions.DeviceEdit)) return ServiceResult.Fail("Device.Forbidden");
        await using var db = await _dbFactory.CreateContextAsync();
        var device = await db.Devices.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == id);
        if (device is null) return ServiceResult.Fail("Common.NotFound", id);
        if (device.ArchivedAtUtc.HasValue && device.ProvisionStatus == DeviceProvisionStatus.Archived) return ServiceResult.Ok();
        device.ArchivedAtUtc ??= DateTime.UtcNow;
        device.ProvisionStatus = DeviceProvisionStatus.Archived;
        await AuditAsync(db, device, AuditType.Archive, "Device archived");
        await db.SaveChangesAsync();
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> RestoreDeviceAsync(int id)
    {
        if (!await CanDeviceAsync(Constants.Permissions.DeviceEdit)) return ServiceResult.Fail("Device.Forbidden");
        await using var db = await _dbFactory.CreateContextAsync();
        var device = await db.Devices.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == id);
        if (device is null) return ServiceResult.Fail("Common.NotFound", id);
        if (device.ArchivedAtUtc is null && device.ProvisionStatus != DeviceProvisionStatus.Archived) return ServiceResult.Ok();
        device.ArchivedAtUtc = null;
        if (device.ProvisionStatus == DeviceProvisionStatus.Archived)
            device.ProvisionStatus = device.ProvisionedAtUtc is not null && !string.IsNullOrEmpty(device.InstallationId)
                ? DeviceProvisionStatus.Provisioned : DeviceProvisionStatus.PreProvisioned;
        await AuditAsync(db, device, AuditType.Restore, "Device restored");
        await db.SaveChangesAsync();
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> DeleteDeviceAsync(int id)
    {
        if (!await CanDeviceAsync(Constants.Permissions.DeviceEdit)) return ServiceResult.Fail("Device.Forbidden");
        await using var db = await _dbFactory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var device = await db.Devices.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == id);
        if (device is null) return ServiceResult.Fail("Common.NotFound", id);
        // Retain the directory principal for rediscovery and the audit snapshot for history.
        if (await db.Devices.IgnoreQueryFilters().Where(x => x.Id == id).ExecuteDeleteAsync() != 1)
            return ServiceResult.Fail("Common.NotFound", id);
        await AuditAsync(db, device, AuditType.Delete, "Device deleted");
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult<Comment>> AddCommentAsync(int deviceId, string text)
    {
        var user = (await authenticationStateProvider.GetAuthenticationStateAsync()).User;
        var username = user.Identity?.Name ?? user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (user.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(username) ||
            !(await authorization.AuthorizeAsync(user, Constants.Permissions.DeviceEdit)).Succeeded)
            return ServiceResult<Comment>.Fail("Device.Forbidden");
        if (string.IsNullOrWhiteSpace(text)) return ServiceResult<Comment>.Fail("Device.CommentRequired");

        await using var db = await _dbFactory.CreateContextAsync();
        var device = await db.Devices.SingleOrDefaultAsync(x => x.Id == deviceId);
        if (device is null)
            return ServiceResult<Comment>.Fail("Common.NotFound", deviceId);
        var comment = new Comment { CommentText = text.Trim(), Username = username, CommentedAtUtc = DateTime.UtcNow };
        db.DeviceComments.Add(new DeviceComment { DeviceId = deviceId, Comment = comment });
        DeviceAuditWriter.Add(db, user, device, AuditType.Update, "Comment added");
        await db.SaveChangesAsync();
        return ServiceResult<Comment>.Ok(comment);
    }

    public async Task<ServiceResult> UpdateGeneralAsync(int id, string? description, IEnumerable<int> selectedTags)
    {
        if (!await CanDeviceAsync(Constants.Permissions.DeviceEdit)) return ServiceResult.Fail("Device.Forbidden");
        await using var db = await _dbFactory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"device-general:" + id}, 0))");
        var device = await db.Devices.SingleOrDefaultAsync(x => x.Id == id);
        if (device is null) return ServiceResult.Fail("Common.NotFound", id);

        var tagIds = selectedTags.Distinct().ToArray();
        if (await db.Tags.CountAsync(x => tagIds.Contains(x.Id)) != tagIds.Length)
            return ServiceResult.Fail("Device.TagsUnavailable");

        device.Description = description?.Trim();
        await db.DeviceTags.Where(x => x.DeviceId == id).ExecuteDeleteAsync();
        db.DeviceTags.AddRange(tagIds.Select(tagId => new DeviceTag { DeviceId = id, TagId = tagId }));
        await AuditAsync(db, device, AuditType.Update, "Description and tags saved");
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> SaveCustomAttributesAsync(int deviceId, IReadOnlyDictionary<int, string> values)
    {
        if (!await CanDeviceAsync(Constants.Permissions.DeviceEdit)) return ServiceResult.Fail("Device.Forbidden");
        await using var db = await _dbFactory.CreateContextAsync();
        var device = await db.Devices.SingleOrDefaultAsync(x => x.Id == deviceId);
        if (device is null)
            return ServiceResult.Fail("Common.NotFound", deviceId);

        var attributeIds = values.Keys.ToArray();
        if (await db.CustomAttributes.CountAsync(x => attributeIds.Contains(x.Id)) != attributeIds.Length)
            return ServiceResult.Fail("Device.AttributesUnavailable");

        var existing = await db.DeviceCustomAttributes
            .Where(x => x.DeviceId == deviceId && attributeIds.Contains(x.CustomAttributeId))
            .OrderByDescending(x => x.Id).ToListAsync();
        foreach (var (attributeId, value) in values)
        {
            var matches = existing.Where(x => x.CustomAttributeId == attributeId).ToList();
            if (string.IsNullOrEmpty(value))
                db.DeviceCustomAttributes.RemoveRange(matches);
            else if (matches.Count > 0)
            {
                matches[0].Value = value;
                db.DeviceCustomAttributes.RemoveRange(matches.Skip(1));
            }
            else
                db.DeviceCustomAttributes.Add(new DeviceCustomAttribute
                {
                    DeviceId = deviceId, CustomAttributeId = attributeId, Value = value
                });
        }

        await AuditAsync(db, device, AuditType.Update, "Custom attributes saved");
        await db.SaveChangesAsync();
        return ServiceResult.Ok();
    }

    private async Task AuditAsync(CloneDeployDbContext db, Device device, AuditType type, string section) =>
        DeviceAuditWriter.Add(db, (await authenticationStateProvider.GetAuthenticationStateAsync()).User, device, type, section);

    private async Task<bool> CanDeviceAsync(string permission)
    {
        var user = (await authenticationStateProvider.GetAuthenticationStateAsync()).User;
        return user.Identity?.IsAuthenticated == true && (await authorization.AuthorizeAsync(user, permission)).Succeeded;
    }

    public async Task<ServiceResult<List<DeviceSearchResultDto>>> SearchDevices(DeviceFilterDto filter)
    {
        if (!await CanDeviceAsync(Constants.Permissions.DeviceView)) return ServiceResult<List<DeviceSearchResultDto>>.Fail("Device.Forbidden");
        var searchText = filter.SearchText?.Trim() ?? "";
        var hasGuidSearch = Guid.TryParse(searchText, out var searchGuid);

        var allowedStatuses = new List<DeviceProvisionStatus>();

        if (filter.IsActive == true)
            allowedStatuses.Add(DeviceProvisionStatus.Provisioned);
        else if (filter.IsUnmanaged == true)
            allowedStatuses.Add(DeviceProvisionStatus.Unmanaged);
        else
            allowedStatuses.AddRange([
                DeviceProvisionStatus.Provisioned,
                DeviceProvisionStatus.Unmanaged,
                DeviceProvisionStatus.IntermediateInstalled,
                DeviceProvisionStatus.NotStarted,
                DeviceProvisionStatus.PendingPreProvision,
                DeviceProvisionStatus.PendingProvisionApproval,
                DeviceProvisionStatus.PendingConfirmation,
                DeviceProvisionStatus.PendingReset,
                DeviceProvisionStatus.PreProvisioned,
                DeviceProvisionStatus.ProvisionApproved,
                DeviceProvisionStatus.Reset,
                DeviceProvisionStatus.Error,
                DeviceProvisionStatus.Archived,
                DeviceProvisionStatus.FullReset
            ]);

        await using var db = await _dbFactory.CreateContextAsync();

        var query = db.Devices
            .AsNoTracking()
            .Where(device =>
                (allowedStatuses.Contains(device.ProvisionStatus)) &&
                (
                    string.IsNullOrEmpty(searchText) ||
                    EF.Functions.ILike(device.Name, $"%{searchText}%") ||
                    EF.Functions.ILike(device.InstallationId!, $"%{searchText}%") ||
                    EF.Functions.ILike(device.UUID!, $"%{searchText}%") ||
                    EF.Functions.ILike(device.LastKnownIp!, $"%{searchText}%") ||
                    (hasGuidSearch && device.Guid == searchGuid) ||
                    EF.Functions.ILike(device.Bios!.SerialNumber!, $"%{searchText}%") ||
                    device.Logins.Any(x =>
                        EF.Functions.ILike(x.UserName, $"%{searchText}%")) ||
                    device.CustomAttributes.Any(x =>
                        EF.Functions.ILike(x.Value, $"%{searchText}%"))
                ) &&
                (
                    filter.TagIds.Count == 0 ||
                    filter.CategoryType == "Any" ||
                    (filter.CategoryType == "Or" &&
                     device.Tags.Any(tag => filter.TagIds.Contains(tag.TagId))) ||
                    (filter.CategoryType == "And" &&
                     filter.TagIds.All(tagId =>
                         device.Tags.Any(tag => tag.TagId == tagId)))
                ));

        query = query.OrderByDescending(x => x.LastCheckinAtUtc);
        if (filter.Limit != 0)
            query = query.Take(filter.Limit);

        return ServiceResult<List<DeviceSearchResultDto>>.Ok(await query
            .Select(device => new DeviceSearchResultDto
            {
                Id = device.Id,
                Name = device.Name,
                LastCheckinTime = device.LastCheckinAtUtc,
                LastIp = device.LastKnownIp,
                ClientVersion = device.ClientVersion,
                AgentPlatform = device.AgentPlatform,
                LastLoggedInUser = device.Logins
                    .OrderByDescending(x => x.Id)
                    .Select(x => x.UserName)
                    .FirstOrDefault(),
                ProvisionedTime = device.ProvisionedAtUtc,
                Status = device.SocketConnection!.ConnectionId != null
                    ? "Connected"
                    : "Disconnected",
                Manufacturer = device.ComputerSystem!.Manufacturer,
                Description = device.Description,
                Model = device.ComputerSystem!.Model,
                Domain = device.ComputerSystem!.Domain,
                OsName = device.Os!.Caption,
                OsVersion = device.Os!.Version,
                OsBuild = device.Os!.BuildNumber,
                ProvisionStatus = device.ProvisionStatus,
                ArchivedAtUtc = device.ArchivedAtUtc,
                IsAdSync = device.DirectorySourceId != null && device.DirectoryObjectGuid != null,
                AdDisabled = device.DirectoryObject != null && device.DirectoryObject.Disabled
            })
            .ToListAsync());
    }

    public async Task<ServiceResult<Device>> CreateDeviceAsync(string name)
    {
        if (!await CanDeviceAsync(Constants.Permissions.DeviceAdd)) return ServiceResult<Device>.Fail("Device.Forbidden");
        if (string.IsNullOrWhiteSpace(name)) return ServiceResult<Device>.Fail("Device.Validation");
        name = name.Trim();
        await using var db = await _dbFactory.CreateContextAsync();

        var device = new Device
        {
            Name = name,
            Guid = Guid.NewGuid()
        };

        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        await AuditAsync(db, device, AuditType.Create, "Device created");
        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        return ServiceResult<Device>.Ok(device);
    }

    public async Task<ServiceResult<List<Device>>> CreateDevicesAsync(string names)
    {
        if (!await CanDeviceAsync(Constants.Permissions.DeviceAdd)) return ServiceResult<List<Device>>.Fail("Device.Forbidden");
        if (string.IsNullOrWhiteSpace(names) || names.Length > 65_000)
            return ServiceResult<List<Device>>.Fail("Device.BulkNamesInvalid");
        var requested = names.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (requested.Length is < 1 or > 500 || requested.Any(name => name.Length > 128 ||
                name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.')))
            return ServiceResult<List<Device>>.Fail("Device.BulkNamesInvalid");

        await using var db = await _dbFactory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        // Enrollment and imaging registration take this same lock before matching names.
        await db.Database.ExecuteSqlRawAsync("LOCK TABLE \"Devices\" IN SHARE ROW EXCLUSIVE MODE");
        var normalized = requested.Select(name => name.ToLowerInvariant()).ToArray();
        var existing = (await db.Devices.IgnoreQueryFilters().Where(d => normalized.Contains(d.Name.ToLower()))
            .Select(d => d.Name).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var created = requested.Where(name => !existing.Contains(name)).Select(name => new Device
        {
            Name = name, Guid = Guid.NewGuid(), ProvisionStatus = DeviceProvisionStatus.PreProvisioned
        }).ToList();
        db.Devices.AddRange(created);
        await db.SaveChangesAsync();
        foreach (var device in created) await AuditAsync(db, device, AuditType.Create, "Device created");
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return ServiceResult<List<Device>>.Ok(created);
    }
}
