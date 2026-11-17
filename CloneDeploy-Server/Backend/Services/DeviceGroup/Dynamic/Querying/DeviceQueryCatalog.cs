using CloneDeploy_Server.Backend.Data.Models;
using System.Reflection;
using System.Text.RegularExpressions;

namespace CloneDeploy_Server.Backend.Services.DynamicGroups;

/// <summary>
/// Lists the device information and matching choices available when building group rules.
/// </summary>
public static class DeviceQueryCatalog
{
    // Explicit allowlist: adding a model property never makes it queryable automatically.
    public static readonly QueryScopeDto[] Scopes =
    [
        new("", "Device", typeof(Device), null, false, ["Name", "Description", "AgentPlatform", "LastKnownIp", "ClientVersion", "ProvisionStatus", "LastCheckinAtUtc", "LastInventoryAtUtc", "ProvisionedAtUtc", "IsAdSync", "AdDisabled"]),
        new("Os", "Operating system", typeof(DeviceOs), "Os", false, ["Caption", "Version", "BuildNumber", "OSArchitecture", "NativeArchitecture", "ReleaseId", "UacStatus", "LocalTimeZone", "LocationEnabled", "UpdateServer"]),
        new("ComputerSystem", "Computer system", typeof(DeviceComputerSystem), "ComputerSystem", false, ["Manufacturer", "Model", "Domain", "Workgroup", "Memory"]),
        new("Bios", "BIOS", typeof(DeviceBios), "Bios", false, ["SerialNumber", "Version", "SMBIOSBIOSVersion"]),
        new("Processor", "Processor", typeof(DeviceProcessor), "Processor", false, ["Name", "Speed", "Cores"]),
        new("Software", "Installed software", typeof(Software), "Softwares", true, ["Name", "Version", "Major", "Minor", "Build", "Revision"]),
        new("Certificates", "Certificate", typeof(Certificate), "Certificates", true, ["Store", "Subject", "FriendlyName", "Thumbprint", "Serial", "Issuer", "NotBeforeUtc", "NotAfterUtc"]),
        new("Tags", "Device tag", typeof(Tag), "Tags", true, ["Name", "Description"]),
        new("WindowsUpdates", "Windows update", typeof(DeviceWindowsUpdate), "WindowsUpdates", true, ["WindowsUpdate.Title", "WindowsUpdate.Category", "IsInstalled", "LastDeploymentChangedAtUtc"]),
        new("HardDrives", "Hard drive", typeof(DeviceHardDrive), "HardDrives", true, ["Model", "FirmwareRevision", "SerialNumber", "SizeMb", "Status"]),
        new("Nics", "Network adapter", typeof(DeviceNic), "Nics", true, ["Name", "Description", "Type", "Mac", "Status", "Speed", "Ips", "Gateways"]),
        new("LogicalVolumes", "Logical volume", typeof(DeviceLogicalVolume), "LogicalVolumes", true, ["Drive", "FreeSpaceGB", "FreeSpacePercent", "SizeGB"]),
        new("Printers", "Printer", typeof(DevicePrinter), "Printers", true, ["Name", "DriverName", "Local", "Network", "ShareName", "SystemName"]),
        new("AntiVirus", "Antivirus", typeof(DeviceAntiVirus), "AntiVirus", false, ["DisplayName", "Provider", "RealtimeScanner", "DefinitionStatus", "ProductState"]),
        new("Firewall", "Firewall", typeof(DeviceFirewall), "Firewall", false, ["DomainEnabled", "PrivateEnabled", "PublicEnabled"]),
        new("BitLocker", "BitLocker", typeof(DeviceBitLocker), "BitLocker", false, ["DriveLetter", "Status"]),
        new("Gpu", "GPU", typeof(DeviceGpu), "Gpu", false, ["Name", "Memory"]),
        new("CustomAttributes", "Custom attribute", typeof(DeviceCustomAttribute), "CustomAttributes", true, ["CustomAttributeId", "Value"]),
        new("CustomInventories", "Custom inventory", typeof(DeviceCustomInventory), "CustomInventories", true, ["ScriptActionId", "Value"])
    ];

    public static IEnumerable<QueryScopeDto> AvailableScopes => Scopes.Where(x => x.Fields.Length > 0);

    public static QueryScopeDto Get(string scope) => AvailableScopes.SingleOrDefault(x => x.Id == scope)
                                                     ?? throw new InvalidOperationException("Unknown inventory category.");

    public static PropertyInfo Property(string scope, string field)
    {
        var category = Get(scope);
        if (!category.Fields.Contains(field)) throw new InvalidOperationException("Unknown query field.");
        // Keep existing saved query field names while the values now come from the relationship.
        if (scope == "" && field is "IsAdSync" or "AdDisabled")
            return typeof(DirectoryObject).GetProperty(nameof(DirectoryObject.Disabled))!;
        var type = category.Type;
        PropertyInfo? property = null;
        foreach (var part in field.Split('.'))
        {
            property = type.GetProperty(part)!;
            type = property.PropertyType;
        }

        return property!;
    }

    public static string Label(string value) => value switch
    {
        "AgentPlatform" => "Platform",
        "CustomAttributeId" => "Custom attribute", "ScriptActionId" => "Inventory script",
        _ => Regex.Replace(value.Split('.').Last(), "(?<=[a-z])([A-Z])", " $1")
    };

    public static QueryOperator[] Operators(string scope, string field)
    {
        var type = Nullable.GetUnderlyingType(Property(scope, field).PropertyType) ?? Property(scope, field).PropertyType;
        if (field is "CustomAttributeId" or "ScriptActionId" || scope == "" && field == "AgentPlatform") return [QueryOperator.Equal];
        if (type == typeof(string)) return [QueryOperator.Equal, QueryOperator.Contains, QueryOperator.StartsWith, QueryOperator.IsEmpty];
        if (type == typeof(bool) || type.IsEnum) return [QueryOperator.Equal];
        return [QueryOperator.Equal, QueryOperator.GreaterThan, QueryOperator.GreaterOrEqual, QueryOperator.LessThan, QueryOperator.LessOrEqual, QueryOperator.IsEmpty];
    }
}
