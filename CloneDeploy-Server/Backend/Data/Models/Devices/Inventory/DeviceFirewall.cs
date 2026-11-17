namespace CloneDeploy_Server.Backend.Data.Models
{
    public class DeviceFirewall
    {
        public int Id { get; set; }
        
        public int DeviceId { get; set; }
        
        public bool DomainEnabled { get; set; }
        
        public bool PrivateEnabled { get; set; }
        
        public bool PublicEnabled { get; set; }
        
        //navigation properties
        public Device? Device { get; set; }
    }
}