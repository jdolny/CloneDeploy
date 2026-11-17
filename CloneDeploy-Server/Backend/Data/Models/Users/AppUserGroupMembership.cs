namespace CloneDeploy_Server.Backend.Data.Models
{
    /// <summary>
    /// Links an AppUser to an AppUserGroup
    /// </summary>
    public class AppUserGroupMembership
    {
        public int Id { get; set; }
        
        public int AppUserGroupId { get; set; }
        
        public int AppUserId { get; set; }
        
        //navigation properties
        public AppUserGroup? AppUserGroup { get; set; }
        public AppUser? AppUser { get; set; }
    }
}