namespace CloneDeploy_Server.Backend.Data.Models
{
    public class DeviceBitLocker
    {
        public int Id { get; set; }
        
        public int DeviceId { get; set; }
      
        public string? DriveLetter { get; set; }
       
        public int Status { get; set; }
        
        //navigation properties
        public Device? Device { get; set; }
    }
}