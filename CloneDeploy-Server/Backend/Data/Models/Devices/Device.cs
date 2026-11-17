using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.Backend.Data.Models;

public class Device
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public Guid Guid { get; set; }
    public DeviceProvisionStatus ProvisionStatus { get; set; } = DeviceProvisionStatus.NotStarted;
    public string? InstallationId { get; set; }
    public DateTime? ProvisionedAtUtc { get; set; }
    public DateTime? LastCheckinAtUtc { get; set; }
    public DateTime? LastInventoryAtUtc { get; set; }
    public DateTime? ArchivedAtUtc { get; set; }
    public string? LastKnownIp { get; set; } 
    public string? ClientVersion { get; set; }
    public string AgentPlatform { get; set; } = CloneDeploy_Agent.Contracts.AgentPlatform.Windows;
    public string? ImagingMac { get; set; }
    public string? UUID { get; set; } 
    public string? LastSocketResult { get; set; }

    //relationships
    public int? DirectorySourceId { get; set; }
    public Guid? DirectoryObjectGuid { get; set; }
    public int? ImageId { get; set; } 
    public int? DeploymentProfileId { get; set; }
    public DeploymentProfile? DeploymentProfile { get; set; }
    
    //navigation properties
    public DirectoryObject? DirectoryObject { get; set; }
    public ActiveImagingTask? ActiveImagingTask { get; set; }
    public DeviceAntiVirus? AntiVirus { get; set; }
    public ICollection<DeviceAttachment> Attachments { get; set; } = [];
    public DeviceBios? Bios { get; set; }
    public DeviceBitLocker? BitLocker { get; set; }
    public ICollection<DeviceCertificate> Certificates { get; set; } = [];
    public ICollection<DeviceComment> Comment { get; set; } = [];
    public DeviceComputerSystem? ComputerSystem { get; set; }
    public ICollection<DeviceCustomAttribute> CustomAttributes { get; set; } = [];
    public ICollection<DeviceCustomInventory> CustomInventories { get; set; } = [];
    public DeviceFirewall? Firewall { get; set; }
    public DeviceGpu? Gpu { get; set; }
    public ICollection<DeviceGroupMembership> Groups { get; set; } = [];
    public ICollection<DeviceHardDrive> HardDrives { get; set; } = [];
    public Image? Image { get; set; }
    public ICollection<DeviceImagingLog> ImagingLogs { get; set; } = [];
    public ICollection<DeviceLogicalVolume> LogicalVolumes { get; set; } = [];
    public ICollection<DeviceLogin> Logins { get; set; } = [];
    public ICollection<DeviceNic> Nics { get; set; } = [];
    public DeviceOs? Os { get; set; }
    public ICollection<DevicePrinter> Printers { get; set; } = [];
    public DeviceProcessor? Processor { get; set; }
    public SocketConnection? SocketConnection { get; set; }
    public ICollection<DeviceSoftware> Softwares { get; set; } = [];
    public ICollection<DeviceTag> Tags { get; set; } = [];
    public DeviceWindowsUpdateScan? WindowsUpdateScan { get; set; }
    public ICollection<DeviceWindowsUpdate> WindowsUpdates { get; set; } = [];
    
    
    
    
    
    
    
    
    
}
