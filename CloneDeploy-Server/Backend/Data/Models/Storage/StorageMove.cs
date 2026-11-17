using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

public sealed class StorageMove
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int SourceId { get; set; }
    public int DestinationId { get; set; }
    public StoragePurpose Purposes { get; set; }
    public string State { get; set; } = "Queued";
    public string? Error { get; set; }
    public long BytesCompleted { get; set; }
    public long BytesTotal { get; set; }
    public string FilesJson { get; set; } = "[]";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CleanupAfterUtc { get; set; }
}

public sealed class StorageMoveConfiguration : IEntityTypeConfiguration<StorageMove>
{
    public void Configure(EntityTypeBuilder<StorageMove> b)
    {
        b.ToTable("StorageMoves");
        b.Property(x => x.State).HasMaxLength(32);
        b.Property(x => x.Error).HasMaxLength(2000);
        b.HasOne<StorageProvider>().WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<StorageProvider>().WithMany().HasForeignKey(x => x.DestinationId).OnDelete(DeleteBehavior.Restrict);
    }
}
