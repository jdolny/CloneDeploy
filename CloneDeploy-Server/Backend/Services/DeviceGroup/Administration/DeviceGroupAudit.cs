using System.Security.Claims;
using System.Text.Json;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.Backend.Services;

public sealed record DeviceGroupAuditEntry(int Id, DateTime LoggedAtUtc, string UserName, AuditType Operation, string Name, string Details);

internal static class DeviceGroupAudit
{
    internal static void Add(CloneDeployDbContext db, ClaimsPrincipal user, int id, string name, AuditType operation, string section)
    {
        if (!int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            throw new UnauthorizedAccessException("An authenticated user is required to audit group changes.");
        db.AuditLogs.Add(new AuditLog { AppUserId = userId, UserName = user.Identity?.Name ?? userId.ToString(),
            ObjectType = nameof(DeviceGroup), ObjectId = id, ObjectName = name, AuditType = operation,
            ObjectJson = JsonSerializer.Serialize(new { Section = section }) });
    }

    internal static DeviceGroupAuditEntry? Read(AuditLog audit, int groupId)
    {
        if (audit.ObjectId != groupId) return null;
        var details = "";
        var wake = false;
        try
        {
            using var json = JsonDocument.Parse(audit.ObjectJson);
            if (json.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (json.RootElement.TryGetProperty("Section", out var section) && section.ValueKind == JsonValueKind.String)
                    details = section.GetString() ?? "";
                if (json.RootElement.TryGetProperty("groupId", out var group) && group.ValueKind == JsonValueKind.Number && group.TryGetInt32(out var id)) wake = id == groupId;
            }
        }
        catch (JsonException) { }
        if (audit.ObjectType != nameof(DeviceGroup) && !(audit.ObjectType == "Wake" && wake)) return null;
        return new(audit.Id, audit.LoggedAtUtc, audit.UserName, audit.AuditType, audit.ObjectName,
            audit.ObjectType == "Wake" ? "Group wake-up requested" : details);
    }
}
