using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

public class AppUserOptions
{
    public int Id { get; set; }
    public int AppUserId { get; set; }
    public ColorMode ColorMode { get; set; }
    public string? Language { get; set; }

    public AppUser AppUser { get; set; } = null!;
}

public class AppUserOptionsConfiguration : IEntityTypeConfiguration<AppUserOptions>
{
    public void Configure(EntityTypeBuilder<AppUserOptions> builder)
    {
        builder.ToTable("AppUserPreferences");
        builder.Property(x => x.Language).HasMaxLength(32);
        builder.HasOne(x => x.AppUser).WithOne(x => x.AppUserOptions).HasForeignKey<AppUserOptions>(x => x.AppUserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
