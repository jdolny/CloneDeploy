namespace CloneDeploy_Server.Backend.Enum;

[Flags]
public enum StoragePurpose
{
    Imaging = 1,
    DeviceManagement = 2,
    All = Imaging | DeviceManagement
}
