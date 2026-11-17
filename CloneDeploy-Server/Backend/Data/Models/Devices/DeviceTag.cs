namespace CloneDeploy_Server.Backend.Data.Models
{
    public class DeviceTag
    {
        public int Id { get; set; }
        
        public int DeviceId { get; set; }
        
        public int TagId { get; set; }
        
        //navigation properties
        public Device? Device { get; set; }
        public Tag? Tag { get; set; }
    }
}