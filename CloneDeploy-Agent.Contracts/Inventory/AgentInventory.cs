namespace CloneDeploy_Agent.Contracts;

public sealed record AgentInventoryRequest(Guid RequestId, Guid DeviceGuid, DateTimeOffset ExpiresAtUtc);
public sealed record AgentInventoryReceipt(Guid RequestId, bool Saved, string Detail);

// Null sections were unavailable; empty collections are successful scans with no results.
public sealed class AgentInventory
{
    public int SchemaVersion { get; set; } = 1;
    public string? HardwareUuid { get; set; }
    public List<string> Warnings { get; set; } = [];
    public InventoryComputerSystem? ComputerSystem { get; set; }
    public InventoryOs? Os { get; set; }
    public InventoryBios? Bios { get; set; }
    public InventoryProcessor? Processor { get; set; }
    public InventoryGpu? Gpu { get; set; }
    public InventoryAntiVirus? AntiVirus { get; set; }
    public InventoryBitLocker? BitLocker { get; set; }
    public InventoryFirewall? Firewall { get; set; }
    public List<InventoryHardDrive>? HardDrives { get; set; }
    public List<InventoryLogicalVolume>? LogicalVolumes { get; set; }
    public List<InventoryNic>? Nics { get; set; }
    public List<InventoryPrinter>? Printers { get; set; }
    public List<InventorySoftware>? Software { get; set; }
    public List<InventoryCertificate>? Certificate { get; set; }
    public List<InventoryWindowsUpdate>? WindowsUpdate { get; set; }
    public InventoryWindowsUpdateScan? WindowsUpdateScan { get; set; }
    public InventoryCustomCollection? CustomInventory { get; set; }
}

public sealed record InventoryCustomCollection(Guid PolicyRunId, List<InventoryCustomValue> Results);
public sealed record InventoryCustomValue(Guid ScriptGuid, string Value)
{
    public const int MaximumLength = 4000;
    public const int MaximumResults = 256;
}

public sealed class InventoryComputerSystem
{
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? Name { get; set; }
    public string? Domain { get; set; }
    public string? Workgroup { get; set; }
    public int Memory { get; set; }
}

public sealed class InventoryOs
{
    public bool? LocationEnabled { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public DateTime? LastLocationUpdatedAtUtc { get; set; }
    public string? Caption { get; set; }
    public string? Version { get; set; }
    public string? BuildNumber { get; set; }
    public string? OSArchitecture { get; set; }
    public string? NativeArchitecture { get; set; }
    public string? ReleaseId { get; set; }
    public string? UacStatus { get; set; }
    public string? LocalTimeZone { get; set; }
    public string? UpdateServer { get; set; }
    public string? SUStargetGroup { get; set; }
}

public sealed class InventoryBios
{
    public string? SerialNumber { get; set; }
    public string? Version { get; set; }
    public string? SMBIOSBIOSVersion { get; set; }
}

public sealed class InventoryProcessor
{
    public string? Name { get; set; }
    public int Speed { get; set; }
    public int Cores { get; set; }
}

public sealed class InventoryGpu
{
    public string? Name { get; set; }
    public int Memory { get; set; }
}

public sealed class InventoryAntiVirus
{
    public string? DisplayName { get; set; }
    public string? Provider { get; set; }
    public string? RealtimeScanner { get; set; }
    public string? DefinitionStatus { get; set; }
    public int ProductState { get; set; }
}

public sealed class InventoryBitLocker
{
    public string? DriveLetter { get; set; }
    public int Status { get; set; }
}

public sealed class InventoryFirewall
{
    public bool DomainEnabled { get; set; }
    public bool PrivateEnabled { get; set; }
    public bool PublicEnabled { get; set; }
}

public sealed class InventoryHardDrive
{
    public string? Model { get; set; }
    public string? FirmwareRevision { get; set; }
    public string? SerialNumber { get; set; }
    public int SizeMb { get; set; }
    public string? Status { get; set; }
}

public sealed class InventoryLogicalVolume
{
    public required string Drive { get; set; }
    public int FreeSpaceGB { get; set; }
    public int FreeSpacePercent { get; set; }
    public int SizeGB { get; set; }
}

public sealed class InventoryNic
{
    public string? IpNetworks { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Type { get; set; }
    public string? Mac { get; set; }
    public string? Status { get; set; }
    public long Speed { get; set; }
    public string? Ips { get; set; }
    public string? Gateways { get; set; }
}

public sealed class InventoryPrinter
{
    public string? Name { get; set; }
    public string? DriverName { get; set; }
    public bool Local { get; set; }
    public bool Network { get; set; }
    public string? ShareName { get; set; }
    public string? SystemName { get; set; }
}

public sealed class InventorySoftware
{
    public string? Name { get; set; }
    public string? Version { get; set; }
    public int Major { get; set; }
    public int Minor { get; set; }
    public int Build { get; set; }
    public int Revision { get; set; }
    public string? UninstallString { get; set; }
}

public sealed class InventoryCertificate
{
    public required string Store { get; set; }
    public required string Subject { get; set; }
    public string? FriendlyName { get; set; }
    public required string Thumbprint { get; set; }
    public required string Serial { get; set; }
    public required string Issuer { get; set; }
    public DateTime NotBeforeUtc { get; set; }
    public DateTime NotAfterUtc { get; set; }
}

public sealed class InventoryWindowsUpdate
{
    public int? RevisionNumber { get; set; }
    public string[]? KBArticleIds { get; set; }
    public List<InventoryUpdateCategory>? Categories { get; set; }
    public string? MsrcSeverity { get; set; }
    public string? Type { get; set; }
    public bool? IsDownloaded { get; set; }
    public bool? IsHidden { get; set; }
    public bool? BrowseOnly { get; set; }
    public string? Title { get; set; }
    public string? Category { get; set; }
    public string? UpdateId { get; set; }
    public bool IsInstalled { get; set; }
    public DateTime? LastDeploymentChangedAtUtc { get; set; }
}

public sealed class InventoryUpdateCategory
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Type { get; set; }
}

public sealed class InventoryWindowsUpdateScan
{
    public InventoryWindowsUpdateConfiguration? Configuration { get; set; }
    public DateTime CollectedAtUtc { get; set; }
    public bool IsOnline { get; set; }
    public bool Succeeded { get; set; }
    public int? ResultCode { get; set; }
    public int? ErrorCode { get; set; }
    public string? SourceName { get; set; }
    public string? SourceId { get; set; }
    // WUA AutomaticUpdates timestamp; not proof of the freshness of every cached result.
    public DateTime? LastSuccessfulSearchAtUtc { get; set; }
    public bool? RebootRequired { get; set; }
}
