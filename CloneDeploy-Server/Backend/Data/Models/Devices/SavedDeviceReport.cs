using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

public sealed class SavedDeviceReport
{
    public int Id { get; set; }
    public int AppUserId { get; set; }
    public string Name { get; set; } = "";
    public string QueryJson { get; set; } = "";
    public string ColumnsJson { get; set; } = "";
    public Guid Revision { get; set; }
}

public sealed class SavedDeviceReportConfiguration : IEntityTypeConfiguration<SavedDeviceReport>
{
    public void Configure(EntityTypeBuilder<SavedDeviceReport> builder)
    {
        builder.Property(x => x.Name).HasMaxLength(200).HasColumnType("citext");
        builder.HasIndex(x => new { x.AppUserId, x.Name }).IsUnique();
        builder.Property(x => x.Revision).IsConcurrencyToken();
        builder.HasOne<AppUser>().WithMany().HasForeignKey(x => x.AppUserId).OnDelete(DeleteBehavior.Cascade);
    }
}
