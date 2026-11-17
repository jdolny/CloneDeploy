namespace CloneDeploy_Server.Backend.Data.Models
{
    public class Comment
    {
        public int Id { get; set; }
        
        public required string CommentText { get; set; }
        
        public required string Username { get; set; }
        
        public DateTime CommentedAtUtc { get; set; } = DateTime.UtcNow;
    }
}