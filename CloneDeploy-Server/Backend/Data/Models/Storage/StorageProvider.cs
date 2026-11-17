using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

public class StorageProvider
{
    public int Id { get; set; }

    public string Name { get; set; } = null!;

    public StorageProviderType Type { get; set; }

    public string Path { get; set; } = null!;

    // Optional OS-managed Linux path; Path remains the client-facing UNC for SMB.
    public string? LinuxMountPath { get; set; }

    public bool IsImagingDefault { get; set; }
    public bool IsManagementDefault { get; set; }

    public bool IsEnabled { get; set; } = true;
    public bool IsDeleted { get; set; }
    public bool ImagingDirectConnect { get; set; }
    public string? WinPeSmbPath { get; set; }

    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public string ImagingSmbPath => ImagingDirectConnect && !string.IsNullOrWhiteSpace(WinPeSmbPath) ? WinPeSmbPath : Path;

    public StoragePurpose Purposes { get; set; } = StoragePurpose.All;
    public string PreferredClientNetworks { get; set; } = "";
    // Null means this provider is accessed by the main application.
    public Guid? SiteServiceId { get; set; }
    public SiteServiceRegistration? SiteService { get; set; }

    public int? ManagedCredentialId { get; set; }

    //navigation property
    public ManagedCredential? ManagedCredential { get; set; }
}

public class StorageProviderConfiguration : IEntityTypeConfiguration<StorageProvider>
{
    public void Configure(EntityTypeBuilder<StorageProvider> builder)
    {
        // Keep referenced IDs as tombstones without offering deleted locations for reads or writes.
        builder.HasQueryFilter(x => !x.IsDeleted);
        builder.Property(x => x.Purposes).HasDefaultValue(StoragePurpose.All).HasSentinel(StoragePurpose.All);
        builder.Property(x => x.PreferredClientNetworks).HasMaxLength(8192).HasDefaultValue("");
        builder.HasIndex(x => x.IsImagingDefault).IsUnique().HasFilter("\"IsImagingDefault\" = TRUE");
        builder.HasIndex(x => x.IsManagementDefault).IsUnique().HasFilter("\"IsManagementDefault\" = TRUE");
        builder.ToTable("StorageProviders", t =>
        {
            t.HasCheckConstraint("CK_StorageProviders_DefaultEnabled", "(NOT \"IsImagingDefault\" OR (\"IsEnabled\" AND \"SiteServiceId\" IS NULL AND (\"Purposes\" & 1) = 1)) AND (NOT \"IsManagementDefault\" OR (\"IsEnabled\" AND \"SiteServiceId\" IS NULL AND (\"Purposes\" & 2) = 2))");
            t.HasCheckConstraint("CK_StorageProviders_Purposes", "\"Purposes\" BETWEEN 1 AND 3");
        });
        builder.HasOne(x => x.SiteService).WithMany().HasForeignKey(x => x.SiteServiceId).OnDelete(DeleteBehavior.Restrict);

        builder.Property(t => t.Name)
            .HasColumnType("citext");

        builder.HasIndex(t => t.Name)
            .IsUnique().HasFilter("NOT \"IsDeleted\"");

        builder.HasIndex(t => t.Path)
            .IsUnique().HasFilter("\"SiteServiceId\" IS NULL AND NOT \"IsDeleted\"");
        builder.HasIndex(t => new { t.SiteServiceId, t.Path }).IsUnique().HasFilter("\"SiteServiceId\" IS NOT NULL AND NOT \"IsDeleted\"");
    }
}
