using System.Security.Claims;
using System.Text;
using System.Text.Json;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed class UserDeviceReportQuery
{
    public bool SelfService { get; set; }
    public string PolicyName { get; set; } = "";
    public int? Result { get; set; }
    public string UserSid { get; set; } = "";
    public string UserName { get; set; } = "";
    public bool ExactMatch { get; set; }
    public DateTime? From { get; set; }
    public DateTime? Through { get; set; }
    public bool IncludeArchived { get; set; }
}

public sealed class UserDeviceReportService(ICloneDeployDbFactory factory, AuthenticationStateProvider authentication,
    IAuthorizationService authorization, ILogger<UserDeviceReportService> logger)
{
    public static readonly List<DeviceReportColumn> Columns =
    [
        new("user", "Recorded username"), new("name", "Device name"), new("first-login", "First recorded login (UTC)"),
        new("last-login", "Latest recorded login (UTC)"), new("login-count", "Recorded login count"),
        new("last-user", "Device's last logged-in user"), new("model", "Model"), new("os", "Operating system"),
        new("last-checkin", "Last check-in (UTC)"), new("archived", "Archived")
    ];
    public static readonly string[] Defaults = ["user", "name", "last-login", "login-count", "last-user", "last-checkin"];
    public static readonly List<DeviceReportColumn> SelfServiceColumns =
    [
        new("user", "Recorded username"), new("sid", "User SID"), new("policy", "Policy"), new("name", "Device name"),
        new("accepted", "Accepted (UTC)"), new("completed", "Completed (UTC)"), new("result", "Result"),
        new("detail", "Reported details"), new("hash", "Policy version"), new("run-id", "Run ID"), new("archived", "Archived")
    ];
    public static readonly string[] SelfServiceDefaults = ["user", "policy", "name", "accepted", "completed", "result"];
    public static string ResultText(DateTime? completed, int? result) => completed is null ? "Awaiting result" : result switch
    { 0 => "Succeeded", 1 => "Failed", 2 => "Skipped", 3 => "Not applicable", _ => "Unknown result" };
    private Task<ServiceResult<T>> Run<T>(Func<Task<ServiceResult<T>>> action) => ServiceOperations.RunAsync(action, logger, "Device.Failed");
    private async Task<int?> UserAsync()
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        return user.Identity?.IsAuthenticated == true && int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            && (await authorization.AuthorizeAsync(user, Constants.Permissions.ReportUsers)).Succeeded ? id : null;
    }
    private static ServiceResult<T> Invalid<T>(string message) => ServiceResult<T>.Fail("Group.Validation", message);
    public Task<ServiceResult<List<SavedUserDeviceReport>>> ListAsync() => Run(async () =>
    {
        if (await UserAsync() is not { } user) return ServiceResult<List<SavedUserDeviceReport>>.Fail("Device.Forbidden");
        await using var db = await factory.CreateContextAsync();
        return ServiceResult<List<SavedUserDeviceReport>>.Ok(await db.Set<SavedUserDeviceReport>().AsNoTracking().Where(x => x.AppUserId == user).OrderBy(x => x.Name).ToListAsync());
    });

    public Task<ServiceResult<SavedUserDeviceReport>> SaveAsync(int id, Guid revision, string name, UserDeviceReportQuery definition, IReadOnlyList<string> columns) => Run(async () =>
    {
        if (await UserAsync() is not { } user) return ServiceResult<SavedUserDeviceReport>.Fail("Device.Forbidden");
        name = name.Trim();
        if (name.Length is < 1 or > 200) return Invalid<SavedUserDeviceReport>("Enter a report name up to 200 characters.");
        await using var db = await factory.CreateContextAsync();
        var (query, selected, error) = await PrepareAsync(db, definition, columns);
        if (error is not null) return Invalid<SavedUserDeviceReport>(error);
        await using var tx = await db.Database.BeginTransactionAsync();
        // Serialize edits to one user's private report library.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(68321, {user})");
        var report = id == 0 ? new SavedUserDeviceReport { AppUserId = user } : await db.Set<SavedUserDeviceReport>().SingleOrDefaultAsync(x => x.Id == id && x.AppUserId == user);
        if (report is null || id != 0 && report.Revision != revision) return Invalid<SavedUserDeviceReport>("This report changed or is unavailable. Reload it before saving.");
        if (await db.Set<SavedUserDeviceReport>().AnyAsync(x => x.AppUserId == user && x.Id != id && x.Name == name)) return Invalid<SavedUserDeviceReport>("You already have a report with this name.");
        report.Name = name;
        report.QueryJson = JsonSerializer.Serialize(definition);
        report.ColumnsJson = JsonSerializer.Serialize(selected!.Select(x => x.Key));
        report.Revision = Guid.NewGuid();
        if (id == 0) db.Add(report);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return ServiceResult<SavedUserDeviceReport>.Ok(report);
    });

    public Task<ServiceResult<bool>> DeleteAsync(int id, Guid revision) => Run(async () =>
    {
        if (await UserAsync() is not { } user) return ServiceResult<bool>.Fail("Device.Forbidden");
        await using var db = await factory.CreateContextAsync();
        var removed = await db.Set<SavedUserDeviceReport>().Where(x => x.Id == id && x.AppUserId == user && x.Revision == revision).ExecuteDeleteAsync();
        return removed == 1 ? ServiceResult<bool>.Ok(true) : Invalid<bool>("This report changed or is unavailable. Reload it before deleting.");
    });


    public Task<ServiceResult<DeviceReportPreview>> PreviewAsync(UserDeviceReportQuery definition, IReadOnlyList<string> columns) => Run(async () =>
    {
        if (await UserAsync() is null) return ServiceResult<DeviceReportPreview>.Fail("Device.Forbidden");
        await using var db = await factory.CreateContextAsync();
        db.Database.SetCommandTimeout(60);
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
        var (query, selected, error) = await PrepareAsync(db, definition, columns);
        if (error is not null) return Invalid<DeviceReportPreview>(error);
        var total = await query!.CountAsync();
        var rows = await Order(query!, definition.SelfService).Take(100).ToListAsync();
        return ServiceResult<DeviceReportPreview>.Ok(new(total, selected!, rows.Select(x => Format(x, selected!)).ToList(), DateTime.UtcNow));
    });

    public Task<ServiceResult<string>> ExportAsync(UserDeviceReportQuery definition, IReadOnlyList<string> columns) => Run(async () =>
    {
        if (await UserAsync() is null) return ServiceResult<string>.Fail("Device.Forbidden");
        await using var db = await factory.CreateContextAsync();
        db.Database.SetCommandTimeout(60);
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
        var (query, selected, error) = await PrepareAsync(db, definition, columns);
        if (error is not null) return Invalid<string>(error);
        var csv = new StringBuilder();
        csv.AppendLine(string.Join(",", selected!.Select(x => DeviceReportColumns.CsvCell(x.Title))));
        await foreach (var row in Order(query!, definition.SelfService).AsAsyncEnumerable())
            csv.AppendLine(string.Join(",", Format(row, selected!).Values.Select(DeviceReportColumns.CsvCell)));
        return ServiceResult<string>.Ok(csv.ToString());
    });

    private static IOrderedQueryable<Row> Order(IQueryable<Row> query, bool selfService) => selfService
        ? query.OrderByDescending(x => x.First).ThenByDescending(x => x.RunId)
        : query.OrderBy(x => x.User).ThenBy(x => x.DeviceId);

    private sealed class Row
    {
        public Guid RunId { get; set; }
        public int? PolicyId { get; set; }
        public string? Sid { get; set; }
        public string? Policy { get; set; }
        public DateTime? Completed { get; set; }
        public int? Result { get; set; }
        public string? Detail { get; set; }
        public string? Hash { get; set; }
        public int DeviceId { get; set; }
        public string User { get; set; } = "";
        public string Name { get; set; } = "";
        public DateTime First { get; set; }
        public DateTime Last { get; set; }
        public int Count { get; set; }
        public string? LastUser { get; set; }
        public string? Model { get; set; }
        public string? Os { get; set; }
        public DateTime? Checkin { get; set; }
        public bool Archived { get; set; }
    }
    private static Task<(IQueryable<Row>? Query, List<DeviceReportColumn>? Columns, string? Error)> PrepareAsync(
        CloneDeployDbContext db, UserDeviceReportQuery definition, IReadOnlyList<string> keys)
    {
        var catalog = definition.SelfService ? SelfServiceColumns : Columns;
        (IQueryable<Row>?, List<DeviceReportColumn>?, string?) result;
        if (keys.Count == 0 || keys.Distinct().Count() != keys.Count || keys.Any(k => !catalog.Any(c => c.Key == k)))
            result = (null, null, "Choose one or more different available columns.");
        else if (definition.UserName.Length > 200 || definition.PolicyName.Length > 200 || definition.UserSid.Length > 184 || definition.Result is < -1 or > 3 || definition.From?.Date > definition.Through?.Date || definition.Through?.Date == DateTime.MaxValue.Date)
            result = (null, null, "Enter a username up to 200 characters and a valid date range.");
        else if (definition.SelfService)
        {
            var runs = db.Set<AgentPolicyRun>().AsNoTracking().Where(x => x.SelfService);
            var user = definition.UserName.Trim().ToLowerInvariant();
            var sid = definition.UserSid.Trim();
            if (user.Length > 0)
                runs = definition.ExactMatch ? runs.Where(x => x.UserName != null && x.UserName.ToLower() == user)
                    : runs.Where(x => x.UserName != null && x.UserName.ToLower().Contains(user));
            if (sid.Length > 0) runs = runs.Where(x => x.UserSid == sid);
            if (definition.From is { } from)
            {
                var utc = DateTime.SpecifyKind(from.Date, DateTimeKind.Utc);
                runs = runs.Where(x => x.StartedAtUtc >= utc);
            }
            if (definition.Through is { } through)
            {
                var utc = DateTime.SpecifyKind(through.Date.AddDays(1), DateTimeKind.Utc);
                runs = runs.Where(x => x.StartedAtUtc < utc);
            }
            if (definition.Result == -1) runs = runs.Where(x => x.FinishedAtUtc == null);
            else if (definition.Result is { } status) runs = runs.Where(x => x.FinishedAtUtc != null && x.Result == status);
            var devices = db.Devices.AsNoTracking();
            if (!definition.IncludeArchived) devices = devices.Where(x => x.ArchivedAtUtc == null && x.ProvisionStatus != DeviceProvisionStatus.Archived);
            var policies = db.Policies.AsNoTracking();
            var policyName = definition.PolicyName.Trim().ToLowerInvariant();
            if (policyName.Length > 0) policies = policies.Where(x => x.Name.ToLower().Contains(policyName));
            var query = from run in runs join device in devices on run.DeviceId equals device.Id
                join policy in policies on run.PolicyId equals policy.Id
                select new Row
                {
                    RunId = run.Id, DeviceId = device.Id, Name = device.Name, User = run.UserName ?? "", Sid = run.UserSid,
                    PolicyId = policy.Id, Policy = policy.Name, First = run.StartedAtUtc, Completed = run.FinishedAtUtc, Result = run.Result,
                    Detail = run.Detail, Hash = run.Hash, Archived = device.ArchivedAtUtc != null || device.ProvisionStatus == DeviceProvisionStatus.Archived
                };
            result = (query, keys.Select(k => catalog.Single(c => c.Key == k)).ToList(), null);
        }
        else
        {
            var logins = db.Set<DeviceLogin>().AsNoTracking().Where(x => x.LoggedInAtUtc != default);
            if (!definition.IncludeArchived)
                logins = logins.Where(x => x.Device!.ArchivedAtUtc == null && x.Device.ProvisionStatus != DeviceProvisionStatus.Archived);
            var user = definition.UserName.Trim().ToLowerInvariant();
            if (user.Length > 0)
                logins = definition.ExactMatch ? logins.Where(x => x.UserName.ToLower() == user) : logins.Where(x => x.UserName.ToLower().Contains(user));
            if (definition.From is { } from)
            {
                var utc = DateTime.SpecifyKind(from.Date, DateTimeKind.Utc);
                logins = logins.Where(x => x.LoggedInAtUtc >= utc);
            }
            if (definition.Through is { } through)
            {
                var utc = DateTime.SpecifyKind(through.Date.AddDays(1), DateTimeKind.Utc);
                logins = logins.Where(x => x.LoggedInAtUtc < utc);
            }
            var grouped = logins.GroupBy(x => new { x.DeviceId, User = x.UserName.ToLower() })
                .Select(g => new { g.Key.DeviceId, g.Key.User, First = g.Min(x => x.LoggedInAtUtc), Last = g.Max(x => x.LoggedInAtUtc), Count = g.Count() });
            var query = from login in grouped join device in db.Devices.AsNoTracking() on login.DeviceId equals device.Id
                select new Row
                {
                    DeviceId = device.Id, Name = device.Name, User = login.User, First = login.First, Last = login.Last, Count = login.Count,
                    LastUser = device.Logins.OrderByDescending(x => x.LoggedInAtUtc).ThenByDescending(x => x.Id).Select(x => x.UserName.ToLower()).FirstOrDefault(),
                    Model = device.ComputerSystem == null ? null : device.ComputerSystem.Model,
                    Os = device.Os == null ? null : device.Os.Caption, Checkin = device.LastCheckinAtUtc,
                    Archived = device.ArchivedAtUtc != null || device.ProvisionStatus == DeviceProvisionStatus.Archived
                };
            result = (query, keys.Select(k => Columns.Single(c => c.Key == k)).ToList(), null);
        }
        return Task.FromResult(result);
    }
    private static DeviceReportResultRow Format(Row row, List<DeviceReportColumn> columns) => new(row.DeviceId,
        columns.Select(c => DeviceReportColumns.Text(c.Key switch
        {
            "sid" => row.Sid, "policy" => row.Policy, "accepted" => row.First, "completed" => row.Completed,
            "result" => ResultText(row.Completed, row.Result), "detail" => row.Detail, "hash" => row.Hash, "run-id" => row.RunId,
            "user" => row.User, "name" => row.Name, "first-login" => row.First, "last-login" => row.Last,
            "login-count" => row.Count, "last-user" => row.User == row.LastUser ? "Yes" : "No", "model" => row.Model,
            "os" => row.Os, "last-checkin" => row.Checkin, "archived" => row.Archived ? "Yes" : "No", _ => null
        })).ToList())
        { ReportedDetail = row.Detail, PolicyHash = row.Hash, PolicyName = row.Policy, PolicyId = row.PolicyId, RunId = row.RunId == Guid.Empty ? null : row.RunId };
}
