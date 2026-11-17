using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

public sealed class AppUserGroupDirectoryMapping
{
    public int AppUserGroupId { get; set; }
    public int DirectorySourceId { get; set; }
    public Guid ObjectGuid { get; set; }
    public string DisplayName { get; set; } = "";
}

public sealed class AppUserGroupDirectoryMappingConfiguration : IEntityTypeConfiguration<AppUserGroupDirectoryMapping>
{
    public void Configure(EntityTypeBuilder<AppUserGroupDirectoryMapping> builder)
    {
        builder.HasKey(x => new { x.AppUserGroupId, x.DirectorySourceId, x.ObjectGuid });
        builder.HasOne<AppUserGroup>().WithMany().HasForeignKey(x => x.AppUserGroupId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<DirectorySource>().WithMany().HasForeignKey(x => x.DirectorySourceId).OnDelete(DeleteBehavior.Cascade);
        // Retain the selected object ID when a group disappears; never substitute by name.
    }
}
