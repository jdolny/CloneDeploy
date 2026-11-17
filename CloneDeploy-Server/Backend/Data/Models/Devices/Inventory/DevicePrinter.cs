namespace CloneDeploy_Server.Backend.Data.Models
{
    public class DevicePrinter
    {
        public int Id { get; set; }
        
        public int DeviceId { get; set; }
        
        public string? Name { get; set; }
        
        public string? DriverName { get; set; }
        
        public bool Local { get; set; }
        
        public bool Network { get; set; }
        
        public string? ShareName { get; set; }
        
        public string? SystemName { get; set; }
        
        //navigation properties
        public Device? Device { get; set; }
    }
}
