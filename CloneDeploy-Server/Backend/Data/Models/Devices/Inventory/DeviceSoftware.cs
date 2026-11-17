namespace CloneDeploy_Server.Backend.Data.Models
{
    public class DeviceSoftware
    {
        public long Id { get; set; }
        
        public int DeviceId { get; set; }
        
        public int SoftwareId { get; set; }

        public string? UninstallString { get; set; }
        
        //navigation properties
        public Device? Device { get; set; }
        public Software? Software { get; set; }
    }
}
