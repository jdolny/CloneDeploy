using CloneDeploy_Server.Backend.Enum;

namespace CloneDeploy_Server.Backend.Data.Models;

public class AppCertificate
{
    public int Id { get; set; }
    
    public required string Serial { get; set; }
    
    public required string SubjectName { get; set; }
    
    public required byte[] PfxBlob { get; set; }
    
    public required string PasswordEncrypted { get; set; }
    
    public DateTime NotBeforeUtc { get; set; }
    
    public DateTime NotAfterUtc { get; set; }
    
    public bool IsRevoked { get; set; }
    
    public DateTime? RevokedDateUtc { get; set; }
    
    public CertificateType Type { get; set; }
}