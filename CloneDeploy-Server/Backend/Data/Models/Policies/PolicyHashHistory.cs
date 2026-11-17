namespace CloneDeploy_Server.Backend.Data.Models
{
    public class PolicyHashHistory
    {
        public int Id { get; set; }
        
        public int PolicyId { get; set; }
        
        public required string Hash { get; set; }
        
        public required string Json { get; set; }
        
        public DateTime ModifiedAtUtc { get; set; }
        
        //navigation properties
        public Policy? Policy { get; set; }
    }
}