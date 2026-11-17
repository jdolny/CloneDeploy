namespace CloneDeploy_Server.Backend.Data.Models
{
    public class DeviceImagingLog
    {
        public int Id { get; set; }
        
        public int DeviceId { get; set; }
        
        public required string Contents { get; set; }     
        
        public DateTime LoggedAtUtc { get; set; } = DateTime.UtcNow;
        
        public string? Mac { get; set; }
        
        public string? SubType { get; set; }
        
        public string? Type { get; set; }
        
        //navigation properties
        public Device? Device { get; set; }
    }
}
