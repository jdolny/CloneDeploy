using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

public sealed class StorageReadLease
{
    public Guid Id { get; set; }
    public string ContentKey { get; set; } = "";
    public int StorageProviderId { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
}

public sealed class StorageReadLeaseConfiguration : IEntityTypeConfiguration<StorageReadLease>
{
    public void Configure(EntityTypeBuilder<StorageReadLease> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.ContentKey).HasMaxLength(200);
        b.HasOne<StorageProvider>().WithMany().HasForeignKey(x => x.StorageProviderId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.StorageProviderId, x.ExpiresAtUtc });
        b.HasIndex(x => x.ExpiresAtUtc);
    }
}
