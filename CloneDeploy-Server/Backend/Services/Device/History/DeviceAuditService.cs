using System.Text.Json;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed record DeviceAuditEntry(int Id, DateTime LoggedAtUtc, string UserName, AuditType Operation,
    string Category, string Name, string Details);

public sealed class DeviceAuditService(ICloneDeployDbFactory factory, AuthenticationStateProvider authentication,
    IAuthorizationService authorization, ILogger<DeviceAuditService> logger)
{
    private static readonly string[] DeviceTypes = [nameof(Device), "RemoteTerminal", "RemoteDesktop", "DeviceTools", "DeviceCheckIn", "DeviceInventory"];

    public Task<ServiceResult<List<DeviceAuditEntry>>> GetAsync(int deviceId) => ServiceOperations.RunAsync(async () =>
    {
        if (!(await authorization.AuthorizeAsync((await authentication.GetAuthenticationStateAsync()).User,
                Constants.Permissions.DeviceView)).Succeeded)
            return ServiceResult<List<DeviceAuditEntry>>.Fail("Device.Forbidden");
        await using var db = await factory.CreateContextAsync();
        var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == deviceId);
        if (device is null) return ServiceResult<List<DeviceAuditEntry>>.Fail("Common.NotFound", deviceId);
        var tasks = await db.DeviceTasks.AsNoTracking().Where(x => x.DeviceId == deviceId)
            .Select(x => new { x.Id, x.DeploymentId }).ToListAsync();
        var taskIds = tasks.Select(x => x.Id.ToString()).ToArray();
        var deploymentIds = tasks.Select(x => x.DeploymentId.ToString()).Distinct().ToArray();
        var idText = deviceId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var guidText = device.Guid.ToString();
        // JSON text is only a coarse database filter. Parse exact identities below; never match by name.
        var candidates = db.AuditLogs.AsNoTracking().Where(x =>
                (DeviceTypes.Contains(x.ObjectType) && x.ObjectId == deviceId) ||
                (x.ObjectType == nameof(DeviceTask) && taskIds.Contains(x.ObjectName)) ||
                (x.ObjectType == nameof(ActionDeployment) && deploymentIds.Contains(x.ObjectName)) ||
                x.ObjectJson.Contains(idText) || x.ObjectJson.ToLower().Contains(guidText))
            .OrderByDescending(x => x.LoggedAtUtc).ThenByDescending(x => x.Id);
        var rows = new List<DeviceAuditEntry>();
        await foreach (var audit in candidates.AsAsyncEnumerable())
        {
            JsonDocument? document = null;
            try { document = JsonDocument.Parse(audit.ObjectJson); } catch (JsonException) { }
            using (document)
            {
                if (!(DeviceTypes.Contains(audit.ObjectType) && audit.ObjectId == deviceId) &&
                    !(audit.ObjectType == nameof(DeviceTask) && taskIds.Contains(audit.ObjectName)) &&
                    !(audit.ObjectType == nameof(ActionDeployment) && deploymentIds.Contains(audit.ObjectName)) &&
                    !(document is not null && ReferencesDevice(document.RootElement, deviceId, device.Guid))) continue;
                rows.Add(new(audit.Id, audit.LoggedAtUtc, audit.UserName, audit.AuditType, audit.ObjectType,
                    audit.ObjectName, document is null ? "" : Describe(document.RootElement)));
            }
        }
        return ServiceResult<List<DeviceAuditEntry>>.Ok(rows);
    }, logger, "Device.Failed");

    private static bool ReferencesDevice(JsonElement value, int id, Guid guid)
    {
        if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().Any(x => ReferencesDevice(x, id, guid));
        if (value.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in value.EnumerateObject())
        {
            if (property.Name.Equals("DeviceId", StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var candidate) && candidate == id) return true;
            if (property.Name.Equals("DeviceGuid", StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String && property.Value.TryGetGuid(out var candidateGuid) && candidateGuid == guid) return true;
            if (property.Name.Equals("DeviceIds", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.Array &&
                property.Value.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out var member) && member == id)) return true;
            if (ReferencesDevice(property.Value, id, guid)) return true;
        }
        return false;
    }

    private static string Describe(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return "";
        // Display event metadata only; arbitrary audit payloads can contain sensitive configuration.
        string[] fields = ["Section", "Operation", "Status", "Assigned", "Acknowledged", "OldName", "NewName", "Shell", "Identity", "Requested", "Sent", "Success"];
        var metadata = string.Join(" · ", value.EnumerateObject().Where(x => fields.Contains(x.Name) &&
            x.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            .Select(x => $"{x.Name}: {x.Value}"));
        if (value.TryGetProperty("request", out var request) && request.ValueKind == JsonValueKind.Object &&
            request.TryGetProperty("Operation", out var operation) && operation.ValueKind == JsonValueKind.Number && operation.TryGetInt32(out var code) &&
            System.Enum.IsDefined(typeof(CloneDeploy_Agent.Contracts.AgentDeviceOperation), code))
            metadata = $"{(CloneDeploy_Agent.Contracts.AgentDeviceOperation)code} · {metadata}".TrimEnd(' ', '·');
        return metadata;
    }
}
