using System.Security.Claims;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Auth;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Controllers.Agent;

/// <summary>Returns display-only metadata for policies delivered to the authenticated device.</summary>
[ApiController, Route("api/agent/v1/portal")]
[Authorize(Policy = AgentCertificateAuthentication.PolicyName)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AgentPortalController(ICloneDeployDbFactory factory) : ControllerBase
{
    private const int MaxRequestBytes = 1024 * 1024;
    private const int MaxReceiptBatchSize = 50;
    private const int MaxNotificationIdLength = 80;
    private const int MaxNotificationTitleLength = 200;
    private const int MaxNotificationBodyLength = 2000;
    private const int NotificationLifetimeDays = 7;
    private const int MaxUserNameLength = 256;
    private const int MaxUserSidLength = 184;
    private const string MacUserSidPrefix = "macos:";

    [HttpPost("notifications/receipts")]
    [RequestSizeLimit(MaxRequestBytes)]
    public async Task<IActionResult> NotificationReceipts([FromBody] AgentNotificationReceipt[] reports, CancellationToken ct)
    {
        if (!Request.IsHttps || reports.Length is < 1 or > MaxReceiptBatchSize) return BadRequest();
        var deviceId = int.Parse(User.FindFirstValue(AgentCertificateAuthentication.DeviceIdClaim)!);
        await using var db = await factory.CreateContextAsync();
        var isMac = await db.Devices.AnyAsync(x => x.Id == deviceId && x.AgentPlatform == AgentPlatform.MacOS, ct);
        foreach (var r in reports)
            if (r is null || r.Notification is not { RequireAcknowledgment: true } m || string.IsNullOrWhiteSpace(m.Id) || m.Id.Length > MaxNotificationIdLength ||
                string.IsNullOrWhiteSpace(m.Title) || m.Title.Length > MaxNotificationTitleLength || string.IsNullOrWhiteSpace(m.Body) || m.Body.Length > MaxNotificationBodyLength ||
                m.CreatedAt > DateTimeOffset.MaxValue.AddDays(-NotificationLifetimeDays) || r.UserSid is null || r.UserName is null || r.UserName.Length > MaxUserNameLength || r.UserSid.Length > MaxUserSidLength ||
                (r.UserSid.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(r.UserSid, @"\AS-1-\d+(?:-\d+){1,15}\z") &&
                    !(isMac && r.UserSid.StartsWith(MacUserSidPrefix, StringComparison.Ordinal) && uint.TryParse(r.UserSid.AsSpan(MacUserSidPrefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var uid) && uid > 0 && r.UserSid == $"macos:{uid}")) ||
                (m.AcknowledgedAt is not null && r.UserSid.Length == 0)) return BadRequest();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var acknowledgments = new List<AgentNotificationAcknowledgment>();
        var identities = new Dictionary<string, string>();
        foreach (var r in reports)
        {
            var m = r.Notification;
            var expiresAtUtc = (m.ExpiresAt ?? m.CreatedAt.AddDays(NotificationLifetimeDays)).UtcDateTime;
            if (!identities.TryGetValue(r.UserSid, out var userKey))
            {
                var (status, users) = r.UserSid.StartsWith(MacUserSidPrefix, StringComparison.Ordinal)
                    ? ("Local", new List<DirectoryObject>())
                    : await DirectoryIdentity.UsersAsync(db, r.UserSid, ct);
                // Only a verified directory identity can roam. Unknown/local users remain device-specific.
                userKey = status == "Matched"
                    ? "directory:" + (!string.IsNullOrWhiteSpace(users[0].ObjectSid) ? users[0].ObjectSid : $"{users[0].DirectorySourceId}:{users[0].ObjectGuid:N}")
                    : $"device:{deviceId}:{r.UserSid}";
                identities[r.UserSid] = userKey;
            }
            // A late pending report must never undo a saved acknowledgment.
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ""DeviceNotificationReceipts"" (""DeviceId"",""NotificationId"",""UserSid"",""UserName"",""Title"",""Body"",""DeliveredAtUtc"",""AcknowledgedAtUtc"",""UserKey"",""ExpiresAtUtc"")
                VALUES ({deviceId},{m.Id},{r.UserSid},{r.UserName},{m.Title},{m.Body},{m.CreatedAt.UtcDateTime},{(m.AcknowledgedAt.HasValue ? m.AcknowledgedAt.Value.UtcDateTime : (DateTime?)null)},{userKey},{expiresAtUtc})
                ON CONFLICT (""DeviceId"",""NotificationId"",""UserSid"") DO UPDATE SET
                ""AcknowledgedAtUtc""=COALESCE(""DeviceNotificationReceipts"".""AcknowledgedAtUtc"",EXCLUDED.""AcknowledgedAtUtc""),
                ""UserKey""=EXCLUDED.""UserKey"", ""ExpiresAtUtc""=EXCLUDED.""ExpiresAtUtc""", ct);
            if (m.AcknowledgedAt is { } acknowledged)
                await db.Database.ExecuteSqlInterpolatedAsync($@"
                    INSERT INTO ""UserNotificationAcknowledgments"" (""NotificationId"",""UserKey"",""AcknowledgedAtUtc"",""AcknowledgedOnDeviceId"")
                    VALUES ({m.Id},{userKey},{acknowledged.UtcDateTime},{deviceId})
                    ON CONFLICT (""NotificationId"",""UserKey"") DO NOTHING", ct);
            if (r.UserSid.Length > 0)
            {
                var saved = await db.Set<UserNotificationAcknowledgment>().AsNoTracking()
                    .SingleOrDefaultAsync(x => x.NotificationId == m.Id && x.UserKey == userKey, ct);
                if (saved is not null)
                    acknowledgments.Add(new(m.Id, r.UserSid, new DateTimeOffset(saved.AcknowledgedAtUtc, TimeSpan.Zero)));
            }
        }
        await transaction.CommitAsync(ct);
        return Ok(acknowledgments);
    }

    [HttpGet("available")]
    public async Task<ActionResult<AgentPortalPolicy[]>> Available([FromQuery] string sid, CancellationToken ct)
    {
        if (!Request.IsHttps || string.IsNullOrWhiteSpace(sid) || sid.Length > MaxUserSidLength) return BadRequest();
        var deviceId = int.Parse(User.FindFirstValue(AgentCertificateAuthentication.DeviceIdClaim)!);
        await using var db = await factory.CreateContextAsync();
        var policies = await PolicyAudienceDelivery.ReadAsync(db, deviceId, sid, true, ct);
        var ids = policies.Select(x => x.Policy.Id).ToArray();
        var access = await db.Set<PolicyUserAccess>().AsNoTracking().Where(x => ids.Contains(x.PolicyId))
            .Select(x => new { x.PolicyId, x.DisplayName, x.Description, x.Icon, x.IconHash, x.Category })
            .ToDictionaryAsync(x => x.PolicyId, ct);
        var systemIds = policies.Where(x => !PolicyUserContext.UsesUserContext(x.Policy.Settings)).Select(x => x.Policy.Id).ToArray();
        var hashes = policies.Select(x => x.Hash).ToArray();
        // Only the published version: a previous version may have used a different execution context.
        var history = await db.Set<AgentPolicyRun>().AsNoTracking()
            .Where(r => r.DeviceId == deviceId && ids.Contains(r.PolicyId) && hashes.Contains(r.Hash) &&
                (systemIds.Contains(r.PolicyId) || r.UserSid == sid))
            .GroupBy(r => r.PolicyId)
            .Select(g => g.OrderByDescending(r => r.StartedAtUtc).ThenByDescending(r => r.Id)
                .Select(r => new { r.PolicyId, r.StartedAtUtc, r.FinishedAtUtc, r.Result }).First()).ToListAsync(ct);
        var lastRuns = history.ToDictionary(r => r.PolicyId);
        return policies.Select(x =>
        {
            var display = access.GetValueOrDefault(x.Policy.Id);
            return new AgentPortalPolicy(x.Policy.Id, display?.DisplayName ?? x.Policy.Name, x.Policy.ExecutionPriority)
            {
                Description = display?.Description,
                Icon = display?.Icon, IconHash = display?.IconHash,
                LastRun = lastRuns.TryGetValue(x.Policy.Id, out var run) ? new(run.StartedAtUtc, run.FinishedAtUtc, run.Result) : null,
                Category = display?.Category is { } category ? new(category.Id, category.Name, category.Icon, category.SortOrder) : null
            };
        }).ToArray();
    }
    [HttpGet("icons/{policyId:int}")]
    public async Task<IActionResult> Icon(int policyId, [FromQuery] string sid, CancellationToken ct)
    {
        if (!Request.IsHttps || string.IsNullOrWhiteSpace(sid) || sid.Length > MaxUserSidLength) return BadRequest();
        var deviceId = int.Parse(User.FindFirstValue(AgentCertificateAuthentication.DeviceIdClaim)!);
        await using var db = await factory.CreateContextAsync();
        if (!(await PolicyAudienceDelivery.ReadAsync(db, deviceId, sid, true, ct)).Any(x => x.Policy.Id == policyId)) return NotFound();
        var png = await db.Set<PolicyUserAccess>().Where(x => x.PolicyId == policyId).Select(x => x.IconPng).SingleOrDefaultAsync(ct);
        return png is null ? NotFound() : File(png, "image/png");
    }

    // The enrolled agent supplies the SID read from its authenticated local pipe caller.
    // This lookup identifies a directory record; it does not authorize policy execution.
    [HttpGet("identity")]
    public async Task<ActionResult<AgentPortalIdentity>> Identity([FromQuery] string sid, CancellationToken ct)
    {
        if (!Request.IsHttps || string.IsNullOrWhiteSpace(sid) || sid.Length > MaxUserSidLength ||
            !System.Text.RegularExpressions.Regex.IsMatch(sid, @"\AS-1-\d+(?:-\d+){1,15}\z"))
            return BadRequest();
        await using var db = await factory.CreateContextAsync();
        return await DirectoryPortalIdentity.ResolveAsync(db, sid, ct);
    }

    [HttpGet("policies")]
    public async Task<ActionResult<AgentPortalPolicy[]>> Policies(CancellationToken ct)
    {
        if (!Request.IsHttps) return BadRequest();
        var deviceId = int.Parse(User.FindFirstValue(AgentCertificateAuthentication.DeviceIdClaim)!);
        await using var db = await factory.CreateContextAsync();
        var policies = await PolicyDelivery.ReadAsync(db, deviceId, ct);
        var ids = policies.Select(x => x.Policy.Id).ToArray();
        var hashes = policies.Select(x => x.Hash).ToArray();
        var history = await db.Set<AgentPolicyRun>().AsNoTracking()
            .Where(r => r.DeviceId == deviceId && ids.Contains(r.PolicyId) && hashes.Contains(r.Hash))
            .GroupBy(r => new { r.PolicyId, r.Hash })
            .Select(g => g.OrderByDescending(r => r.StartedAtUtc).ThenByDescending(r => r.Id)
                .Select(r => new { r.PolicyId, r.Hash, r.StartedAtUtc, r.FinishedAtUtc, r.Result }).First()).ToListAsync(ct);
        var lastRuns = history.ToDictionary(r => (r.PolicyId, r.Hash));
        return policies.Select(x => new AgentPortalPolicy(x.Policy.Id, x.Policy.Name, x.Policy.ExecutionPriority)
        {
            LastRun = lastRuns.TryGetValue((x.Policy.Id, x.Hash), out var run)
                ? new(run.StartedAtUtc, run.FinishedAtUtc, run.Result) : null
        }).ToArray();
    }
}
