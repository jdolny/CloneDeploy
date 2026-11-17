namespace CloneDeploy_Server.Backend.Data.Models
{
    public class WinGetLocaleManifest
    {
        public int Id { get; set; }
        
        public string? PackageIdentifier { get; set; }
        
        public string? PackageVersion { get; set; }
        
        public string? Locale { get; set; }
        
        public string? Publisher { get; set; }
        
        public string? PublisherUrl { get; set; }
        
        public string? PackageName { get; set; }
        
        public string? PackageUrl { get; set; }
        
        public string? License { get; set; }
        
        public string? ShortDescription { get; set; }
        
        public string? ManifestType { get; set; }
        
        public string? Tags { get; set; }
        
        public string? Moniker { get; set; }
        
        public int Major { get; set; }
      
        public int Minor { get; set; }
      
        public int Build { get; set; }
      
        public int Revision { get; set; }

    }
}