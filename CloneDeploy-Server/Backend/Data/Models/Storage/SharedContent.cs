using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

public enum SharedContentKind { WinPe, DriverArchive, DriverOriginal, BootFile, ManagementFile }

// Immutable identity includes the logical resource and its published hash.
// Retired rows survive until their replicas and transfer leases are removed.
public sealed class SharedContent
{
    public required string Id { get; set; }
    public SharedContentKind Kind { get; set; }
    public required string LogicalKey { get; set; }
    public required string Name { get; set; }
    public required string Sha256 { get; set; }
    public long SizeBytes { get; set; }
    public int? SourceProviderId { get; set; }
    public required string SourcePath { get; set; }
    public long ModifiedTicks { get; set; }
    public bool Current { get; set; } = true;
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public string ReplicaRelativePath => CloneDeploy.SiteServices.ReplicaLayout.Shared(Kind.ToString(), LogicalKey, Name, Id);
}

public sealed class SharedContentReplica
{
    public required string ContentId { get; set; }
    public int StorageProviderId { get; set; }
    public ImageReplicaState State { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? RetryAtUtc { get; set; }
    public string? Error { get; set; }
}

public sealed class SharedContentConfiguration : IEntityTypeConfiguration<SharedContent>
{
    public void Configure(EntityTypeBuilder<SharedContent> b)
    {
        b.Property(x => x.Id).HasMaxLength(64);
        b.Property(x => x.LogicalKey).HasMaxLength(1024);
        b.Property(x => x.Name).HasMaxLength(255);
        b.Property(x => x.Sha256).HasMaxLength(64);
        b.Property(x => x.SourcePath).HasMaxLength(4096);
        b.HasIndex(x => new { x.Kind, x.LogicalKey });
    }
}
public sealed class SharedContentReplicaConfiguration : IEntityTypeConfiguration<SharedContentReplica>
{
    public void Configure(EntityTypeBuilder<SharedContentReplica> b)
    {
        b.HasKey(x => new { x.ContentId, x.StorageProviderId });
        b.Property(x => x.Error).HasMaxLength(1000);
        b.HasOne<SharedContent>().WithMany().HasForeignKey(x => x.ContentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<StorageProvider>().WithMany().HasForeignKey(x => x.StorageProviderId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.State, x.RetryAtUtc });
    }
}
