using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

public enum ImageReplicaState { Pending, Copying, Verifying, Ready, Failed, Deleting }

public sealed class ImageReplicationTarget
{
    public int ImageId { get; set; }
    public int StorageProviderId { get; set; }
}

// Also serves as a durable job and deletion tombstone. Deliberately survives
// deletion of its image/version so offline destinations can be cleaned later.
public sealed class ImageReplica
{
    public Guid ImageVersionId { get; set; }
    public int StorageProviderId { get; set; }
    public int ImageId { get; set; }
    public ImageReplicaState State { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? RetryAtUtc { get; set; }
    public string? Error { get; set; }
}

public sealed class ImageReplicationTargetConfiguration : IEntityTypeConfiguration<ImageReplicationTarget>
{
    public void Configure(EntityTypeBuilder<ImageReplicationTarget> b)
    {
        b.HasKey(x => new { x.ImageId, x.StorageProviderId });
        b.HasOne<Image>().WithMany().HasForeignKey(x => x.ImageId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<StorageProvider>().WithMany().HasForeignKey(x => x.StorageProviderId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ImageReplicaConfiguration : IEntityTypeConfiguration<ImageReplica>
{
    public void Configure(EntityTypeBuilder<ImageReplica> b)
    {
        b.HasKey(x => new { x.ImageVersionId, x.StorageProviderId });
        b.Property(x => x.Error).HasMaxLength(1000);
        b.HasOne<StorageProvider>().WithMany().HasForeignKey(x => x.StorageProviderId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.State, x.RetryAtUtc });
    }
}
