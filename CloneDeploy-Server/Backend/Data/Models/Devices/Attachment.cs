namespace CloneDeploy_Server.Backend.Data.Models
{
    public class Attachment
    {
        public int Id { get; set; }
        
        public required string Name { get; set; }
        
        public DateTime AttachedAtUtc { get; set; }
        
        public Guid DirectoryGuid { get; set; }
        
        public required string UserName { get; set; }
    }
}