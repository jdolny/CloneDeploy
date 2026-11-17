namespace CloneDeploy_Server.Backend.Data.Models
{
    public class DeviceAntiVirus
    {
        public int Id { get; set; }
        
        public int DeviceId { get; set; }
        
        public string? DisplayName { get; set; }
        
        public string? Provider { get; set; }
        
        public string? RealtimeScanner { get; set; }
        
        public string? DefinitionStatus { get; set; }
        
        public int ProductState { get; set; }
        
        //navigation properties
        public Device? Device { get; set; }
    }
}