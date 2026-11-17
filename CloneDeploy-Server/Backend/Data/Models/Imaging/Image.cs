using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

public enum ImageFormat { Wim, Ffu }

public class Image
{
    public int Id { get; set; }
    public ImageFormat Format { get; set; }
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;
    public Guid LastUploadGuid { get; set; }
    public required string Name { get; set; } 
    public ICollection<Device> Devices { get; set; } = [];
    public ICollection<ImageTag> Tags { get; set; } = [];
    public ICollection<ImageVersion> Versions { get; set; } = [];
    public bool Protected { get; set; } 
}
public sealed class ImageConfiguration : IEntityTypeConfiguration<Image>
{
    public void Configure(EntityTypeBuilder<Image> builder)
    {
        builder.Property(x => x.Name).HasColumnType("citext");
        builder.HasIndex(x => x.Name).IsUnique();
        builder.HasMany(x => x.Tags).WithOne(x => x.Image).HasForeignKey(x => x.ImageId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.Devices).WithOne(x => x.Image).HasForeignKey(x => x.ImageId).OnDelete(DeleteBehavior.Restrict);
    }
}
