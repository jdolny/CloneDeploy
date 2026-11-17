using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

/// <summary>
/// The state of a directory 
/// </summary>
public class DirectorySyncState
{
    public int DirectorySourceId { get; set; }
    public Guid RootObjectGuid { get; set; }
    public Guid? DomainObjectGuid { get; set; }
    public DateTime LastSuccessfulSyncAtUtc { get; set; }
    
    //navigation properties
    public DirectorySource DirectorySource { get; set; } = null!;
}

public class DirectorySyncStateConfiguration : IEntityTypeConfiguration<DirectorySyncState>
{
    public void Configure(EntityTypeBuilder<DirectorySyncState> builder)
    {
        builder.HasKey(x => x.DirectorySourceId);

        builder.HasOne(x => x.DirectorySource)
            .WithOne(x => x.SyncState)
            .HasForeignKey<DirectorySyncState>(x => x.DirectorySourceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}