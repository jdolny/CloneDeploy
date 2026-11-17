namespace CloneDeploy_Server.Backend.Data.Models
{
    public class DeviceLogin
    {
        public int Id { get; set; }
        
        public int DeviceId { get; set; }
        
        public required string UserName { get; set; }
        
        public DateTime LoggedInAtUtc { get; set; }
        
        public DateTime LoggedOutAtUtc { get; set; }
        
        public int? ClientDatabaseLoginId { get; set; }
        
        //navigation properties
        public Device? Device { get; set; }
    }
}
