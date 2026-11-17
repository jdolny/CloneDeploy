namespace CloneDeploy_Server.Backend.Data.Models
{
    public class DeviceWindowsUpdate
    {
        public int Id { get; set; }
        
        public int DeviceId { get; set; }
        
        public int WindowsUpdateId { get; set; }
        
        public DateTime? LastDeploymentChangedAtUtc { get; set; }
        
        public bool IsInstalled { get; set; }
        public int? RevisionNumber { get; set; }
        public bool? IsDownloaded { get; set; }
        public bool? IsHidden { get; set; }
        public bool? BrowseOnly { get; set; }
        
        //navigation properties
        public Device? Device { get; set; }
        public WindowsUpdate? WindowsUpdate { get; set; }
    }
}