using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

public sealed class ImageUpload
{
    public Guid Id { get; set; }
    public int ImageId { get; set; }
    public ImageFormat Format { get; set; }
    public int StorageProviderId { get; set; }
    public int UserId { get; set; }
    public int? DeviceId { get; set; }
    public string? DeviceName { get; set; }
    public long SizeBytes { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? Sha256 { get; set; }
    public string? CaptureTokenHash { get; set; }
    public string? CaptureBootPlanJson { get; set; }
    public string? CaptureUploadProtected { get; set; }
    public bool CaptureReady { get; set; }
}

public sealed class ImageUploadConfiguration : IEntityTypeConfiguration<ImageUpload>
{
    public void Configure(EntityTypeBuilder<ImageUpload> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Sha256).HasMaxLength(64);
        b.Property(x => x.DeviceName).HasMaxLength(128);
        b.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<Image>().WithMany().HasForeignKey(x => x.ImageId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<StorageProvider>().WithMany().HasForeignKey(x => x.StorageProviderId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.UserId);
    }
}
