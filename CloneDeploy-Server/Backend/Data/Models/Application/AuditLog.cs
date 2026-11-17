using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.Backend.Data.Models
{
    public class AuditLog
    {
        public int Id { get; set; }
        
        public int AppUserId { get; set; }
        
        public AuditType AuditType { get; set; }
        
        public required string ObjectType { get; set; }
        
        public int ObjectId { get; set; }
        
        public required string ObjectName { get; set; }

        public DateTime LoggedAtUtc { get; set; } = DateTime.UtcNow;
        
        public required string UserName { get; set; }
        
        public required string ObjectJson { get; set; }
        
        //navigation property
        public AppUser? AppUser { get; set; }
    }
}