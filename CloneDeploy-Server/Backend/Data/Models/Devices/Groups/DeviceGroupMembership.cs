namespace CloneDeploy_Server.Backend.Data.Models
{
    /// <summary>
    /// Links a Device to a DeviceGroup
    /// </summary>
    public class DeviceGroupMembership
    {
        public int Id { get; set; }
        
        public int DeviceGroupId { get; set; }
        
        public int DeviceId { get; set; }
        
        //navigation properties
        public Device? Device { get; set; }
        public DeviceGroup? DeviceGroup { get; set; }
    }
}