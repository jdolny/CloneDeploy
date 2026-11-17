using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.Backend.Data.Models
{
    public class PolicyHistory
    {
         public int Id { get; set; }
         
         public int PolicyId { get; set; }
         
         public int DeviceId { get; set; }
         
         public PolicyRunResult Result { get; set; }
         
         public required string Hash { get; set; }
         
         public DateTime LastRanAtUtc { get; set; }
         
         public string? UserName { get; set; }
         
         //navigation properties
         public Policy? Policy { get; set; }
         public Device? Device { get; set; }
        
    }
}
