using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

public sealed class ImageVersion
{
    public Guid Id { get; set; }
    public int ImageId { get; set; }
    public int Number { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public DateTime UploadedAtUtc { get; set; }
    public long SizeBytes { get; set; }
    public int StorageProviderId { get; set; }
    public bool IsActive { get; set; }
    public bool IsApproved { get; set; }
    public bool ReplicationApproved { get; set; }
    // Metadata describes the published image; older/manual versions may not have it.
    public ImageFormat? Format { get; set; }
    public string? Layout { get; set; }
    public string? FileName { get; set; }
    public bool? Pipable { get; set; }
    public bool? Optimized { get; set; }
    // Required target capacity from FFU inspection; refresh when the image is optimized.
    public long? MinimumDiskSizeBytes { get; set; }
    public string? Sha256 { get; set; }
    // Historical identity snapshot, retained even if the source device is deleted.
    public int? DeviceId { get; set; }
    public string? DeviceName { get; set; }
    public Image? Image { get; set; }
    // Display-only lookup: a deleted provider must not hide this historical version.
    [NotMapped] public StorageProvider? StorageProvider { get; set; }

    // Stable IDs keep uploads separate even when an image or version is renamed.
    [NotMapped] public string RelativePath => $"Images/{ImageId}/{Id:N}";
}

public sealed class ImageVersionConfiguration : IEntityTypeConfiguration<ImageVersion>
{
    public void Configure(EntityTypeBuilder<ImageVersion> builder)
    {
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Name).HasMaxLength(200);
        builder.Property(x => x.Description).HasMaxLength(2000);
        builder.Property(x => x.Layout).HasMaxLength(32);
        builder.Property(x => x.FileName).HasMaxLength(255);
        builder.Property(x => x.Sha256).HasMaxLength(64);
        builder.Property(x => x.DeviceName).HasMaxLength(128);
        builder.HasIndex(x => new { x.ImageId, x.Number }).IsUnique();
        builder.HasIndex(x => x.ImageId).IsUnique().HasFilter("\"IsActive\" = TRUE");
        builder.HasOne(x => x.Image).WithMany(x => x.Versions).HasForeignKey(x => x.ImageId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<StorageProvider>().WithMany().HasForeignKey(x => x.StorageProviderId).OnDelete(DeleteBehavior.Restrict);
        builder.ToTable("ImageVersions", table =>
        {
            table.HasCheckConstraint("CK_ImageVersions_Number", "\"Number\" > 0");
            table.HasCheckConstraint("CK_ImageVersions_Size", "\"SizeBytes\" >= 0");
            table.HasCheckConstraint("CK_ImageVersions_MinimumDiskSize", "\"MinimumDiskSizeBytes\" IS NULL OR \"MinimumDiskSizeBytes\" > 0");
        });
    }
}
