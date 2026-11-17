namespace CloneDeploy_Server.Backend.Data.Models
{
    public class DeviceProcessor
    {
        public int Id { get; set; }
        
        public int DeviceId { get; set; }
        
        public string? Name { get; set; }
        
        public int Speed { get; set; }
        
        public int Cores { get; set; }
        
        //navigation properties
        public Device? Device { get; set; }
    }
}
