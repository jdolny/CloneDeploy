using CloneDeploy_Server.Backend.Auth;
using CloneDeploy_Server.Backend.Common;
using Microsoft.AspNetCore.Identity;

namespace CloneDeploy_Server.Backend.Data.Models
{
    public class AppUser : IdentityUser<int>
    {
        [System.ComponentModel.DataAnnotations.Schema.NotMapped]
        public bool IsEntraManaged { get; set; }

        [System.ComponentModel.DataAnnotations.Schema.NotMapped]
        public bool IsAdManaged { get; set; }

        [System.ComponentModel.DataAnnotations.Schema.NotMapped]
        public bool IsDirectoryManaged => IsEntraManaged || IsAdManaged;

        [System.ComponentModel.DataAnnotations.Schema.NotMapped]
        public string AccountType => IsEntraManaged && IsAdManaged ? "AD / Entra" : IsAdManaged ? "Active Directory" : IsEntraManaged ? "Entra" : "Local";

        public string Membership { get; set; } = Constants.UserMembership.User;

        // Web MFA policy; administrator password sign-ins always require it.
        public bool EnableMfa { get; set; }
        public bool EnableImagingMfa { get; set; }

        //navigation properties
        public ICollection<AppUserRight> AppUserRights { get; set; } = null!;
        public AppUserOptions? AppUserOptions { get; set; }
    }
}
