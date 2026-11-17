using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

public class AppUserGridColumn
{
    public int Id { get; set; }
    public int AppUserId { get; set; }
    public string GridKey { get; set; } = "devices";
    public string ColumnKey { get; set; } = null!;
    public bool Visible { get; set; }
    public int Order { get; set; }
    public AppUser? AppUser { get; set; }
}

public class AppUserGridColumnConfiguration : IEntityTypeConfiguration<AppUserGridColumn>
{
    public void Configure(EntityTypeBuilder<AppUserGridColumn> builder)
    {
        builder.Property(x => x.GridKey).HasMaxLength(80);
        builder.Property(x => x.ColumnKey).HasMaxLength(120);
        builder.HasIndex(x => new { x.AppUserId, x.GridKey, x.ColumnKey }).IsUnique();
        builder.HasOne(x => x.AppUser).WithMany().HasForeignKey(x => x.AppUserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
