namespace CloneDeploy_Server.Backend.Data.Models
{

    public class DeviceBios
    {
        public int Id { get; set; }
        
        public int DeviceId { get; set; }
        
        public string? SerialNumber { get; set; }
        
        public string? Version { get; set; }
        
        public string? SMBIOSBIOSVersion { get; set; }
        
        //navigation properties
        public Device? Device { get; set; }
    }
}
