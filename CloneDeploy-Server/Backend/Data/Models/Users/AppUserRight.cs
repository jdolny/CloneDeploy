namespace CloneDeploy_Server.Backend.Data.Models
{
    public class AppUserRight
    {
        public int Id { get; set; }
        
        public required string Right { get; set; }
        
        public int AppUserId { get; set; }
        
        //navigational property
        public AppUser? AppUser { get; set; }
    }
}