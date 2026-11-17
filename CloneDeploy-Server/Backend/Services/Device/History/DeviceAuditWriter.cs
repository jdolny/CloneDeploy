using System.Security.Claims;
using System.Text.Json;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.Backend.Services;

internal static class DeviceAuditWriter
{
    // Save with the device change in the caller's transaction. Do not copy free-form values or file contents.
    internal static void Add(CloneDeployDbContext db, ClaimsPrincipal actor, Device device, AuditType type, string section)
    {
        var userId = int.Parse(actor.FindFirstValue(ClaimTypes.NameIdentifier)!);
        db.AuditLogs.Add(new AuditLog
        {
            AppUserId = userId, UserName = actor.Identity?.Name ?? userId.ToString(),
            AuditType = type, ObjectType = nameof(Device), ObjectId = device.Id, ObjectName = device.Name,
            ObjectJson = JsonSerializer.Serialize(new { DeviceId = device.Id, DeviceGuid = device.Guid, Section = section })
        });
    }
}
