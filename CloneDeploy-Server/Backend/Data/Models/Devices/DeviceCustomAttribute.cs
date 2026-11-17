namespace CloneDeploy_Server.Backend.Data.Models
{
    public class DeviceCustomAttribute
    {
        public int Id { get; set; }
        
        public int DeviceId { get; set; }
        
        public int CustomAttributeId { get; set; }
        
        public required string Value { get; set; }
        
        //navigation properties
        public Device? Device { get; set; }
        public CustomAttribute? CustomAttribute { get; set; }
    }
}