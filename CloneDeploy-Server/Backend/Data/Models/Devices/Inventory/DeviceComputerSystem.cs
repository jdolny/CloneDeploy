namespace CloneDeploy_Server.Backend.Data.Models
{
    public class DeviceComputerSystem
    {
        public int Id { get; set; }
        
        public int DeviceId { get; set; }
        
        public string? Manufacturer { get; set; }
        
        public string? Model { get; set; }
        
        public string? Name { get; set; }
        
        public string? Domain { get; set; }
        
        public string? Workgroup { get; set; }
        
        public int Memory { get; set; }
        
        //navigation properties
        public Device? Device { get; set; }
    }
}
