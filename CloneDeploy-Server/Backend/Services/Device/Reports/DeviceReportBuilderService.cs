using System.Security.Claims;
using System.Text;
using System.Text.Json;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using CloneDeploy_Server.Backend.Services.DynamicGroups;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed record DeviceReportOptions(List<DeviceReportColumn> Columns, List<QueryDirectoryChoiceDto> Directories, List<QueryValueChoiceDto> Values);

public sealed class DeviceReportBuilderService(ICloneDeployDbFactory factory, AuthenticationStateProvider authentication,
    IAuthorizationService authorization, ILogger<DeviceReportBuilderService> logger)
{
    private Task<ServiceResult<T>> Run<T>(Func<Task<ServiceResult<T>>> action) => ServiceOperations.RunAsync(action, logger, "Device.Failed");
    private async Task<int?> UserAsync()
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        return user.Identity?.IsAuthenticated == true && int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            && (await authorization.AuthorizeAsync(user, Constants.Permissions.ReportDevices)).Succeeded ? id : null;
    }
    private static ServiceResult<T> Invalid<T>(string message) => ServiceResult<T>.Fail("Group.Validation", message);
    private static async Task<List<DeviceReportColumn>> CatalogAsync(CloneDeployDbContext db) =>
        DeviceReportColumns.Create(await db.CustomAttributes.AsNoTracking().OrderBy(x => x.Name).ToListAsync());

    public Task<ServiceResult<DeviceReportOptions>> OptionsAsync() => Run(async () =>
    {
        if (await UserAsync() is null) return ServiceResult<DeviceReportOptions>.Fail("Device.Forbidden");
        await using var db = await factory.CreateContextAsync();
        var canViewAttributes = (await authorization.AuthorizeAsync((await authentication.GetAuthenticationStateAsync()).User, Constants.Permissions.CustomAttributeView)).Succeeded;
        var columns = DeviceReportColumns.Create(canViewAttributes ? await db.CustomAttributes.AsNoTracking().OrderBy(x => x.Name).ToListAsync() : []);
        var directories = await db.DirectoryObjects.AsNoTracking()
            .Where(x => !x.IsMissing && x.DirectorySource.IsEnabled && !x.DirectorySource.ApplicationAccessOnly
                && (x.Kind == DirectoryObjectKind.Group || x.Kind == DirectoryObjectKind.OrganizationalUnit))
            .OrderBy(x => x.DirectorySource.Name).ThenBy(x => x.DistinguishedName)
            .Select(x => new QueryDirectoryChoiceDto(x.DirectorySourceId, x.ObjectGuid, x.Kind, x.Name + " — " + x.DirectorySource.Name)
                { Path = x.DistinguishedName, DistinguishedName = x.DistinguishedName }).ToListAsync();
        var values = await db.CustomAttributes.Where(x => canViewAttributes).OrderBy(x => x.Name).Select(x => new QueryValueChoiceDto("CustomAttributeId", x.Id.ToString(), x.Name)).ToListAsync();
        values.AddRange(await db.ScriptActions.OrderBy(x => x.Name).Select(x => new QueryValueChoiceDto("ScriptActionId", x.Id.ToString(), x.Name)).ToListAsync());
        values.AddRange(await db.DeviceGroups.OrderBy(x => x.Name).Select(x => new QueryValueChoiceDto("DeviceGroup", x.Id.ToString(), x.Name)).ToListAsync());
        return ServiceResult<DeviceReportOptions>.Ok(new(columns, directories, values));
    });

    public Task<ServiceResult<List<SavedDeviceReport>>> ListAsync() => Run(async () =>
    {
        if (await UserAsync() is not { } user) return ServiceResult<List<SavedDeviceReport>>.Fail("Device.Forbidden");
        await using var db = await factory.CreateContextAsync();
        return ServiceResult<List<SavedDeviceReport>>.Ok(await db.Set<SavedDeviceReport>().AsNoTracking().Where(x => x.AppUserId == user).OrderBy(x => x.Name).ToListAsync());
    });

    public Task<ServiceResult<SavedDeviceReport>> SaveAsync(int id, Guid revision, string name, DeviceQueryDefinitionDto definition, IReadOnlyList<string> columns) => Run(async () =>
    {
        if (await UserAsync() is not { } user) return ServiceResult<SavedDeviceReport>.Fail("Device.Forbidden");
        name = name.Trim();
        if (name.Length is < 1 or > 200) return Invalid<SavedDeviceReport>("Enter a report name up to 200 characters.");
        await using var db = await factory.CreateContextAsync();
        var (query, selected, error) = await PrepareAsync(db, definition, columns);
        if (error is not null) return Invalid<SavedDeviceReport>(error);
        await using var tx = await db.Database.BeginTransactionAsync();
        // Serialize edits to one user's private report library.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(68320, {user})");
        var report = id == 0 ? new SavedDeviceReport { AppUserId = user } : await db.Set<SavedDeviceReport>().SingleOrDefaultAsync(x => x.Id == id && x.AppUserId == user);
        if (report is null || id != 0 && report.Revision != revision) return Invalid<SavedDeviceReport>("This report changed or is unavailable. Reload it before saving.");
        if (await db.Set<SavedDeviceReport>().AnyAsync(x => x.AppUserId == user && x.Id != id && x.Name == name)) return Invalid<SavedDeviceReport>("You already have a report with this name.");
        report.Name = name;
        report.QueryJson = definition.ToJson();
        report.ColumnsJson = JsonSerializer.Serialize(selected!.Select(x => x.Key));
        report.Revision = Guid.NewGuid();
        if (id == 0) db.Add(report);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return ServiceResult<SavedDeviceReport>.Ok(report);
    });

    public Task<ServiceResult<bool>> DeleteAsync(int id, Guid revision) => Run(async () =>
    {
        if (await UserAsync() is not { } user) return ServiceResult<bool>.Fail("Device.Forbidden");
        await using var db = await factory.CreateContextAsync();
        var removed = await db.Set<SavedDeviceReport>().Where(x => x.Id == id && x.AppUserId == user && x.Revision == revision).ExecuteDeleteAsync();
        return removed == 1 ? ServiceResult<bool>.Ok(true) : Invalid<bool>("This report changed or is unavailable. Reload it before deleting.");
    });

    public Task<ServiceResult<DeviceReportPreview>> PreviewAsync(DeviceQueryDefinitionDto definition, IReadOnlyList<string> columns) => Run(async () =>
    {
        if (await UserAsync() is null) return ServiceResult<DeviceReportPreview>.Fail("Device.Forbidden");
        await using var db = await factory.CreateContextAsync();
        db.Database.SetCommandTimeout(60);
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
        var (query, selected, error) = await PrepareAsync(db, definition, columns);
        if (error is not null) return Invalid<DeviceReportPreview>(error);
        if (query is null || selected is null) throw new InvalidOperationException("Report preparation returned no query or columns.");
        var total = await query!.CountAsync();
        var rows = await ReadRowsAsync(db, query.OrderBy(x => x.Id).Take(100), selected!);
        return ServiceResult<DeviceReportPreview>.Ok(new(total, selected!, rows, DateTime.UtcNow));
    });

    public Task<ServiceResult<string>> ExportAsync(DeviceQueryDefinitionDto definition, IReadOnlyList<string> columns) => Run(async () =>
    {
        if (await UserAsync() is null) return ServiceResult<string>.Fail("Device.Forbidden");
        await using var db = await factory.CreateContextAsync();
        db.Database.SetCommandTimeout(60);
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
        var (query, selected, error) = await PrepareAsync(db, definition, columns);
        if (error is not null) return Invalid<string>(error);
        if (query is null || selected is null) throw new InvalidOperationException("Report preparation returned no query or columns.");
        var csv = new StringBuilder();
        csv.Append(string.Join(",", selected!.Select(x => DeviceReportColumns.CsvCell(x.Title)))).Append("\r\n");
        var lastId = 0;
        while (true)
        {
            var batch = await ReadRowsAsync(db, query!.Where(x => x.Id > lastId).OrderBy(x => x.Id).Take(500), selected);
            if (batch.Count == 0) break;
            foreach (var row in batch) csv.Append(string.Join(",", row.Values.Select(DeviceReportColumns.CsvCell))).Append("\r\n");
            lastId = batch[^1].DeviceId;
        }
        return ServiceResult<string>.Ok(csv.ToString());
    });

    private static async Task<(IQueryable<Device>? Query, List<DeviceReportColumn>? Columns, string? Error)> PrepareAsync(
        CloneDeployDbContext db, DeviceQueryDefinitionDto definition, IReadOnlyList<string> keys)
    {
        if (keys.Count is < 1 or > 100 || keys.Distinct().Count() != keys.Count) return (null, null, "Choose between 1 and 100 different columns.");
        var catalog = (await CatalogAsync(db)).ToDictionary(x => x.Key);
        if (keys.Any(x => !catalog.ContainsKey(x))) return (null, null, "A selected column is no longer available. Choose a replacement.");
        try { definition = DeviceQueryDefinitionDto.Parse(definition.ToJson()); }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException) { return (null, null, "The report filters are invalid or too large."); }
        IQueryable<Device>? query;
        string? error = null;
        if (definition.Version == 1 && definition.Root is { Scope: "", Field: null, Negate: false, Children.Count: 0 } && System.Enum.IsDefined(definition.Root.Match))
            query = db.Devices.AsNoTracking().Where(x => x.ArchivedAtUtc == null && x.ProvisionStatus != DeviceProvisionStatus.Archived);
        else (query, error) = await DeviceQueryBuilder.BuildAsync(db, definition, default);
        return (query, keys.Select(x => catalog[x]).ToList(), error);
    }

    private static async Task<List<DeviceReportResultRow>> ReadRowsAsync(CloneDeployDbContext db, IQueryable<Device> query, List<DeviceReportColumn> columns)
    {
        var data = await query.Select(x => new
        {
            x.Id, x.Name, x.Description, x.Guid, x.ClientVersion, x.LastKnownIp, x.LastCheckinAtUtc, x.LastInventoryAtUtc, x.ProvisionedAtUtc, x.ProvisionStatus,
            LastUser = x.Logins.OrderByDescending(l => l.Id).Select(l => l.UserName).FirstOrDefault(),
            Manufacturer = x.ComputerSystem == null ? null : x.ComputerSystem.Manufacturer,
            Model = x.ComputerSystem == null ? null : x.ComputerSystem.Model,
            Domain = x.ComputerSystem == null ? null : x.ComputerSystem.Domain,
            Serial = x.Bios == null ? null : x.Bios.SerialNumber,
            Os = x.Os == null ? null : x.Os.Caption, OsVersion = x.Os == null ? null : x.Os.Version,
            OsBuild = x.Os == null ? null : x.Os.BuildNumber, Architecture = x.Os == null ? null : x.Os.OSArchitecture,
            NativeArchitecture = x.Os == null ? null : x.Os.NativeArchitecture,
            Directory = x.DirectoryObject == null ? null : x.DirectoryObject.DistinguishedName
        }).ToListAsync();
        var ids = data.Select(x => x.Id).ToArray();
        var attributes = columns.Where(x => x.Key.StartsWith("attribute:")).Select(x => int.Parse(x.Key[10..])).ToArray();
        var values = attributes.Length == 0 ? [] : await db.DeviceCustomAttributes.AsNoTracking().Where(x => ids.Contains(x.DeviceId) && attributes.Contains(x.CustomAttributeId)).ToListAsync();
        var valueMap = values.ToLookup(x => (x.DeviceId, x.CustomAttributeId));
        var tags = columns.Any(x => x.Key == "tags")
            ? await db.Set<DeviceTag>().Where(x => ids.Contains(x.DeviceId)).Select(x => new { x.DeviceId, x.Tag!.Name }).ToListAsync() : [];
        var tagMap = tags.ToLookup(x => x.DeviceId, x => x.Name);
        return data.Select(x =>
        {
            object? Value(string key) => key switch
            {
                "name" => x.Name, "description" => x.Description, "id" => x.Id, "guid" => x.Guid,
                "agent-version" => x.ClientVersion, "last-ip" => x.LastKnownIp, "last-checkin" => x.LastCheckinAtUtc,
                "last-inventory" => x.LastInventoryAtUtc, "enrolled" => x.ProvisionedAtUtc, "enrollment" => x.ProvisionStatus,
                "last-user" => x.LastUser, "manufacturer" => x.Manufacturer, "model" => x.Model, "domain" => x.Domain,
                "serial" => x.Serial, "os" => x.Os, "os-version" => x.OsVersion, "os-build" => x.OsBuild,
                "architecture" => x.Architecture, "native-architecture" => x.NativeArchitecture, "directory" => x.Directory,
                "tags" => string.Join("; ", tagMap[x.Id].Order()),
                _ when key.StartsWith("attribute:") => valueMap[(x.Id, int.Parse(key[10..]))].FirstOrDefault()?.Value,
                _ => null
            };
            return new DeviceReportResultRow(x.Id, columns.Select(c => DeviceReportColumns.Text(Value(c.Key))).ToList());
        }).ToList();
    }
}
