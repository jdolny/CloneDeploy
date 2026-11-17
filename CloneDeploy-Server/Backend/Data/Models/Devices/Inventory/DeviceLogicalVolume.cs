namespace CloneDeploy_Server.Backend.Data.Models
{
    public class DeviceLogicalVolume
    {
        public int Id { get; set; }
        
        public int DeviceId { get; set; }
        
        public required string Drive { get; set; }
        
        public int FreeSpaceGB { get; set; }
        
        public int FreeSpacePercent { get; set; }
        
        public int SizeGB { get; set; }
        
        //navigation properties
        public Device? Device { get; set; }
    }
}