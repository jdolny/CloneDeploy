using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models
{
    public class SoftwareAction
    {
        //common
        public int Id { get; set; }
        
        public Guid Guid { get; set; } = Guid.NewGuid();
        
        public SoftwareActionType Type { get; set; }
        public bool Uninstall { get; set; }
        public string? UninstallName { get; set; }
        public string? UninstallVersion { get; set; }

        public string Name { get; set; } = null!;
        
        public string? Description { get; set; }
        
        public string? Arguments { get; set; }
        
        public int Timeout { get; set; }
        
        public bool RedirectStdOut { get; set; }
        
        public bool RedirectStdError { get; set; }

        public string SuccessCodes { get; set; } = "0,1641,3010";
        
        public int? ManagedCredentialId { get; set; }

        //msi
        
        public MsiInstallType MsiInstallType { get; set; }
        
        //msi and exe
        
        public string? Command { get; set; }
        public int? InstallerFileId { get; set; }
        
        public string? AdditionalArguments { get; set; }
        
        
        //winget
        public string? Override { get; set; }
        
        public string? PackageId { get; set; }
        
        public string? PackageVersion { get; set; }
        
        public WinGetInstallType WinGetInstallType { get; set; }
        public WinGetScope WinGetScope { get; set; }
        
        public bool UseDownloadQueue { get; set; }

        public bool KeepUpdated { get; set; }
        
        public bool InstallLatest { get; set; }
     
        
        //navigation properties
        public ActionReference? ActionReference { get; set; }
        public ManagedCredential? ManagedCredential { get; set; }
    }
    
    public class SoftwareActionConfiguration : IEntityTypeConfiguration<SoftwareAction>
    {
        public void Configure(EntityTypeBuilder<SoftwareAction> builder)
        {
            builder.Property(t => t.Name)
                .HasColumnType("citext");
            
            builder.HasIndex(t => t.Name)
                .IsUnique();
            builder.HasOne<StoredFile>().WithMany().HasForeignKey(x => x.InstallerFileId)
                .OnDelete(DeleteBehavior.SetNull);
            
            builder
                .HasOne(x => x.ActionReference)
                .WithOne()
                .HasPrincipalKey<ActionReference>(x => x.ActionGuid)
                .HasForeignKey<SoftwareAction>(x => x.Guid)
                .OnDelete(DeleteBehavior.Cascade);
            
        }
    }
}
