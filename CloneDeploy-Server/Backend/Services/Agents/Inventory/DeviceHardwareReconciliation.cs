using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;
using System.Text.Json;

namespace CloneDeploy_Server.Backend.Services;

internal static class DeviceHardwareReconciliation
{
    private static string? Uuid(string? value) => Guid.TryParse(value, out var id) && id != Guid.Empty &&
        id != Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff") ? id.ToString("N").ToUpperInvariant() : null;
    private static string? Mac(string? value)
    {
        var mac = value?.Replace(":", "").Replace("-", "").Trim().ToUpperInvariant();
        return mac is { Length: 12 } && mac.All(Uri.IsHexDigit) && mac != "000000000000" &&
            (Convert.ToByte(mac[..2], 16) & 1) == 0 ? mac : null;
    }

    public static async Task<(string? Message, int? RemovedDeviceId)> ReconcileAsync(CloneDeployDbContext db, Device active, AgentInventory inventory, CancellationToken ct)
    {
        var uuid = Uuid(active.UUID);
        var macs = (inventory.Nics?.Select(n => Mac(n.Mac)) ?? []).Where(m => m is not null).Cast<string>().Distinct().ToArray();
        if (uuid is null && macs.Length == 0) return (null, null);
        var matches = await db.Devices.IgnoreQueryFilters().Where(d => d.Id != active.Id &&
            ((uuid != null && d.UUID != null && d.UUID.Replace("-", "").Replace("{", "").Replace("}", "").Trim().ToUpper() == uuid) ||
             (d.ImagingMac != null && macs.Contains(d.ImagingMac.Replace(":", "").Replace("-", "").Trim().ToUpper())) ||
             d.Nics.Any(n => n.Mac != null && macs.Contains(n.Mac.Replace(":", "").Replace("-", "").Trim().ToUpper()))))
            .Take(3).ToListAsync(ct);
        if (matches.Count == 0) return (null, null);
        if (matches.Count != 1) return ("Hardware reconciliation needs review: multiple devices match.", null);
        var source = matches[0];
        if (source.ProvisionStatus != DeviceProvisionStatus.Unmanaged || source.ArchivedAtUtc is not null ||
            !string.IsNullOrEmpty(source.InstallationId) ||
            await db.AgentCertificates.AnyAsync(c => c.DeviceId == source.Id, ct) ||
            await db.AgentProvisioningRequests.AnyAsync(p => p.TargetDeviceId == source.Id && p.State != AgentProvisioningState.Denied, ct))
            return ("Hardware reconciliation needs review: the matching device is active, archived or reserved.", null);
        if (uuid is not null && Uuid(source.UUID) is { } stored && stored != uuid)
            return ("Hardware reconciliation needs review: MAC address matches a different UUID.", null);
        // The active record's current image selection wins over a prior WinPE selection.
        // Deployment records and imaging logs retain the source's historical image choices.
        if ((active.DeploymentProfileId is not null && source.DeploymentProfileId is not null && active.DeploymentProfileId != source.DeploymentProfileId) ||
            (active.DirectoryObjectGuid is not null && source.DirectoryObjectGuid is not null &&
                (active.DirectoryObjectGuid != source.DirectoryObjectGuid || active.DirectorySourceId != source.DirectorySourceId)))
            return ("Hardware reconciliation needs review: device assignments conflict.", null);
        if (await db.DeviceCustomAttributes.AnyAsync(s => s.DeviceId == source.Id &&
            db.DeviceCustomAttributes.Any(t => t.DeviceId == active.Id && t.CustomAttributeId == s.CustomAttributeId && t.Value != s.Value), ct))
            return ("Hardware reconciliation needs review: custom attribute values conflict.", null);

        var transaction = db.Database.CurrentTransaction!;
        await transaction.CreateSavepointAsync("hardware_merge", ct);
        try
        {
            // Move every modelled device relationship, including histories, deployments,
            // comments, attachments, groups and assignments. SQL keeps nested relationships intact.
            foreach (var entity in db.Model.GetEntityTypes())
            foreach (var fk in entity.GetForeignKeys().Where(f => f.PrincipalEntityType.ClrType == typeof(Device)))
            {
                if (fk.Properties.Count != 1) throw new InvalidOperationException("Unsupported composite device relationship.");
                var store = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
                var table = Quote(entity.GetTableName()!);
                if (entity.GetSchema() is { } schema) table = Quote(schema) + "." + table;
                var column = Quote(fk.Properties[0].GetColumnName(store)!);
                if (entity.ClrType == typeof(ActiveImagingTask))
                {
                    var sourceTask = await db.ActiveImagingTasks.AsNoTracking().SingleOrDefaultAsync(t => t.DeviceId == source.Id, ct);
                    var activeTask = await db.ActiveImagingTasks.AsNoTracking().SingleOrDefaultAsync(t => t.DeviceId == active.Id, ct);
                    if (sourceTask is not null && activeTask is not null)
                    {
                        // Completed task rows are retained UI snapshots, not running work.
                        // Archive the superseded snapshot rather than losing its details.
                        var archived = Finished(sourceTask) ? sourceTask : Finished(activeTask) ? activeTask : null;
                        if (archived is not null)
                        {
                            var contents = JsonSerializer.Serialize(archived);
                            var loggedAt = DateTime.UtcNow;
                            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"DeviceImagingLogs\" (\"DeviceId\", \"Contents\", \"LoggedAtUtc\", \"Type\", \"SubType\") VALUES ({active.Id}, {contents}, {loggedAt}, 'Imager', 'Reconciled task history')", ct);
                            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"ActiveImagingTasks\" WHERE \"Id\" = {archived.Id}", ct);
                        }
                    }
                }
                var membershipKey = entity.ClrType.Name switch
                {
                    "DeviceGroupMembership" => "DeviceGroupId",
                    "DeviceTag" => "TagId",
                    "DeviceCustomAttribute" => "CustomAttributeId",
                    _ => null
                };
                if (membershipKey is not null)
                {
                    var keyColumn = Quote(entity.FindProperty(membershipKey)!.GetColumnName(store)!);
                    var deduplicateSql = $"DELETE FROM {table} AS s WHERE s.{column} = {{0}} AND EXISTS (SELECT 1 FROM {table} AS t WHERE t.{column} = {{1}} AND t.{keyColumn} = s.{keyColumn})";
                    await db.Database.ExecuteSqlRawAsync(deduplicateSql, [source.Id, active.Id], ct);
                }
                // Agent inventory is authoritative for current one-to-one hardware snapshots.
                // Keep the WinPE snapshot only when the agent has not supplied that section.
                if (fk.IsUnique && entity.ClrType.Namespace == typeof(DeviceBios).Namespace &&
                    entity.ClrType.Name is "DeviceBios" or "DeviceComputerSystem" or "DeviceOs" or "DeviceProcessor" or
                        "DeviceGpu" or "DeviceAntiVirus" or "DeviceBitLocker" or "DeviceFirewall" or "DeviceWindowsUpdateScan")
                {
                    // Identifiers come exclusively from quoted EF metadata; IDs are parameters.
                    var deleteSql = $"DELETE FROM {table} WHERE {column} = {{0}} AND EXISTS (SELECT 1 FROM {table} WHERE {column} = {{1}})";
                    await db.Database.ExecuteSqlRawAsync(deleteSql, [source.Id, active.Id], ct);
                }
                var updateSql = $"UPDATE {table} SET {column} = {{0}} WHERE {column} = {{1}}";
                await db.Database.ExecuteSqlRawAsync(updateSql, [active.Id, source.Id], ct);
            }
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Devices\" WHERE \"Id\" = {source.Id}", ct);
            await transaction.ReleaseSavepointAsync("hardware_merge", ct);
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ForeignKeyViolation)
        {
            await transaction.RollbackToSavepointAsync("hardware_merge", ct);
            db.Entry(source).State = EntityState.Detached;
            return ("Hardware reconciliation needs review: related records conflict; both devices were retained.", null);
        }
        active.ImageId ??= source.ImageId;
        active.DeploymentProfileId ??= source.DeploymentProfileId;
        if (active.DirectoryObjectGuid is null)
        {
            active.DirectorySourceId = source.DirectorySourceId;
            active.DirectoryObjectGuid = source.DirectoryObjectGuid;
        }
        if (!string.IsNullOrEmpty(source.Description) && active.Description != source.Description)
            active.Description = string.IsNullOrEmpty(active.Description) ? source.Description : active.Description + Environment.NewLine + source.Description;
        active.ImagingMac ??= source.ImagingMac;
        active.UUID ??= source.UUID;
        db.Entry(source).State = EntityState.Detached;
        return ($"Reconciled unmanaged device {source.Id} into active device {active.Id}.", source.Id);
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
    private static bool Finished(ActiveImagingTask task) => task.FinishedAtUtc is not null ||
        task.Status is ImagingStatus.Succeeded or ImagingStatus.Failed or ImagingStatus.Cancelled or ImagingStatus.Interrupted;
}
