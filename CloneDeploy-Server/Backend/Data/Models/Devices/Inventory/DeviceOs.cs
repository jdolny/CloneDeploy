namespace CloneDeploy_Server.Backend.Data.Models
{
    public class DeviceOs
    {
        public int Id { get; set; }
        
        public int DeviceId { get; set; }
        
        public string? Caption { get; set; }
        
        public string? Version { get; set; }
        
        public string? BuildNumber { get; set; }
        
        public string? OSArchitecture { get; set; }
        public string? NativeArchitecture { get; set; }
        
        public string? ReleaseId { get; set; }
        
        public string? UacStatus { get; set; }
        
        public string? LocalTimeZone { get; set; }
        
        public string? Latitude { get; set; }
        
        public string? Longitude { get; set; }
        
        public bool LocationEnabled { get; set; }
        
        public DateTime LastLocationUpdatedAtUtc { get; set; }
        
        public string? UpdateServer { get; set; }
        
        public string? SUStargetGroup { get; set; }
        
        //navigation properties
        public Device? Device { get; set; }
    }
}
