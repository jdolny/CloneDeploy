namespace CloneDeploy_Server.Backend.Data.Models
{
    /// <summary>
    /// Links an AppUserGroup to a right to allow app user permissions based on group membership
    /// </summary>
    
    public class AppUserGroupRight
    {
        public int Id { get; set; }
        
        public int AppUserGroupId { get; set; }
        
        public required string Right { get; set; }
        
        //navigation property
        public AppUserGroup? AppUserGroup { get; set; }
    }
}