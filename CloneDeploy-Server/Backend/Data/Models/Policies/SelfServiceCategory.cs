using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace CloneDeploy_Server.Backend.Data.Models;

/// <summary>Server-managed portal navigation category; does not grant policy access.</summary>
public sealed class SelfServiceCategory
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "Tools";
    public int SortOrder { get; set; }
    public Guid Revision { get; set; }
}
public sealed class SelfServiceCategoryConfiguration : IEntityTypeConfiguration<SelfServiceCategory>
{
    public void Configure(EntityTypeBuilder<SelfServiceCategory> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(80);
        builder.Property(x => x.Icon).HasMaxLength(24);
        builder.Property(x => x.Revision).IsConcurrencyToken();
        builder.HasData(
            new SelfServiceCategory { Id = -1, Name = "Software", Icon = "Software", SortOrder = 10 },
            new SelfServiceCategory { Id = -2, Name = "Printers", Icon = "Printers", SortOrder = 20 },
            new SelfServiceCategory { Id = -3, Name = "Maintenance", Icon = "Maintenance", SortOrder = 30 });
    }
}
