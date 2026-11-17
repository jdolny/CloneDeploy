using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

public sealed class ImageContentDeletion
{
    public Guid Id { get; set; }
    public int ImageId { get; set; }
    public int StorageProviderId { get; set; }
    public DateTime QueuedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class ImageContentDeletionConfiguration : IEntityTypeConfiguration<ImageContentDeletion>
{
    public void Configure(EntityTypeBuilder<ImageContentDeletion> builder)
    {
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.HasOne<StorageProvider>().WithMany().HasForeignKey(x => x.StorageProviderId).OnDelete(DeleteBehavior.Restrict);
    }
}
