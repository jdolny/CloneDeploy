namespace CloneDeploy_Server.Backend.Data.Models
{
    /// <summary>
    /// An AppUserGroup that defines properties for all members of the group
    /// </summary>
    public class AppUserGroup
    {
        public bool GrantAdministrator { get; set; }
        public bool RequireWebMfa { get; set; }
        public bool RequireImagingMfa { get; set; }
        public int Id { get; set; }
        
        public required string Name { get; set; }
    }
}