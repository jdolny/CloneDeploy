using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

/// <summary>
/// A directory source, such as active directory or Entra
/// </summary>
public class DirectorySource
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public DirectoryProvider Provider { get; set; }
    public bool ApplicationAccessOnly { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int? ScheduleId { get; set; }
    public LdapDirectorySettings? LdapSettings { get; set; }
    
    //navigaton properties
    public DirectorySyncState SyncState { get; set; } = null!;
}

public class DirectorySourceConfiguration : IEntityTypeConfiguration<DirectorySource>
{
    public void Configure(EntityTypeBuilder<DirectorySource> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name).HasMaxLength(200);
        builder.Property(x => x.Description).HasMaxLength(2000);
        builder.HasOne<Schedule>().WithMany().HasForeignKey(x => x.ScheduleId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.LdapSettings).WithOne().HasForeignKey<LdapDirectorySettings>(x => x.DirectorySourceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

