using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.Backend.Data.Models
{
    public class WinGetManifestDownload
    {
        public int Id { get; set; }
        
        public string? Url { get; set; }
        
        public string? Progress { get; set; }
        
        public ManifestImportStatus Status { get; set; }
        
        public string? ErrorMessage { get; set; }
        
        public DateTime DownloadedAtUtc { get; set; }

    }
}