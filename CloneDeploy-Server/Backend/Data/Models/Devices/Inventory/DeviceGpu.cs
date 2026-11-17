namespace CloneDeploy_Server.Backend.Data.Models
{
    public class DeviceGpu
    {
        public int Id { get; set; }
        
        public int DeviceId { get; set; }
        
        public string? Name { get; set; }
        
        public int Memory { get; set; }
        
        //navigation properties
        public Device? Device { get; set; }
    }
}
