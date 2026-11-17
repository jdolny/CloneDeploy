namespace CloneDeploy_Server.Backend.Data.Models
{
    public class DeviceNic
    {
        public string? IpNetworks { get; set; }
        public int Id { get; set; }
        
        public int DeviceId { get; set; }
        
        public string? Name { get; set; }
        
        public string? Description { get; set; }
        
        public string? Type { get; set; }
        
        public string? Mac { get; set; }
        
        public string? Status { get; set; }
        
        public long Speed { get; set; }
        
        public string? Ips { get; set; }
        
        public string? Gateways { get; set; }
        
        //navigation properties
        public Device? Device { get; set; }
    }
}
