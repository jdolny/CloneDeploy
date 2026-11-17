namespace CloneDeploy_Server.Backend.Data.Models
{
    public class DeviceHardDrive
    {
        public int Id { get; set; }
        
        public int DeviceId { get; set; }
        
        public string? Model { get; set; }
        
        public string? FirmwareRevision { get; set; }
        
        public string? SerialNumber { get; set; }
        
        public int SizeMb { get; set; }
        
        public string? Status { get; set; }
        
        //navigation properties
        public Device? Device { get; set; }
    }
}
