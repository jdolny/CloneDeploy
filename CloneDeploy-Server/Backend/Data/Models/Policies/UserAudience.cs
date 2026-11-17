using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

public sealed class UserAudience
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public Guid Revision { get; set; }
    public List<UserAudienceMember> Members { get; set; } = [];
}

public sealed class UserAudienceMember
{
    public int UserAudienceId { get; set; }
    public int DirectorySourceId { get; set; }
    public Guid ObjectGuid { get; set; }
    public bool Exclude { get; set; }
    public bool IncludeDescendants { get; set; }
    public string DisplayName { get; set; } = "";
}

public sealed class UserAudienceConfiguration : IEntityTypeConfiguration<UserAudience>
{
    public void Configure(EntityTypeBuilder<UserAudience> builder)
    {
        builder.Property(x => x.Name).HasMaxLength(200);
        builder.Property(x => x.Description).HasMaxLength(2000);
        builder.Property(x => x.Revision).IsConcurrencyToken();
        builder.HasMany(x => x.Members).WithOne().HasForeignKey(x => x.UserAudienceId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class UserAudienceMemberConfiguration : IEntityTypeConfiguration<UserAudienceMember>
{
    public void Configure(EntityTypeBuilder<UserAudienceMember> builder)
    {
        builder.HasKey(x => new { x.UserAudienceId, x.DirectorySourceId, x.ObjectGuid, x.Exclude });
        // Retain unavailable selectors when a directory is deleted, particularly exclusions.
    }
}
