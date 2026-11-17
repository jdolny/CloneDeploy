namespace CloneDeploy_Server.Backend.Data.Models
{
    public class DeviceGroupTag
    {
        public int Id { get; set; }
        
        public int DeviceGroupId { get; set; }
        
        public int TagId { get; set; }
        
        //navigation properties
        public DeviceGroup? DeviceGroup { get; set; }
        public Tag? Tag { get; set; }
    }
}