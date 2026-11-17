namespace CloneDeploy_Server.Backend.Data.Models
{
    public class WinGetInstallerManifest
    {
        public int Id { get; set; }
        
        public string? PackageIdentifier { get; set; }
        
        public string? PackageVersion { get; set; }
        
        public string? Scope { get; set; }
    }
}