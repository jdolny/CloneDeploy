using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

public class StoredFile
{
    public int Id { get; set; }
    // Provider ownership must be assigned before file publication or access.
    public int? StorageProviderId { get; set; }
    public Guid DirectoryGuid { get; set; }
    public required string Name { get; set; }
    public FileSourceType SourceType { get; set; }
    public long? Size { get; set; }
    public string? Sha256Hash { get; set; }
    public string? Md5Hash { get; set; }
    public StoredFileStatus Status { get; set; }
    public DateTime? AvailableAtUtc { get; set; }
    public string? ErrorMessage { get; set; }
    public string? Url { get; set; }
    public string? ExpectedSha256Hash { get; set; }
    public bool AutoRefresh { get; set; } = true;
    public DateTime? LastCheckedAtUtc { get; set; }
    public string? Progress { get; set; }
    public DateTime? LastDownloadAttemptAtUtc { get; set; }
}

public class StoredFileConfiguration : IEntityTypeConfiguration<StoredFile>
{
    public void Configure(EntityTypeBuilder<StoredFile> builder)
    {
        builder.HasOne<StorageProvider>().WithMany().HasForeignKey(x => x.StorageProviderId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(x => x.Name).HasColumnType("citext");
        builder.HasIndex(x => new { x.DirectoryGuid, x.Name }).IsUnique();
        builder.ToTable("StoredFiles", table => table.HasCheckConstraint("CK_StoredFiles_Source",
            "(\"SourceType\" = 0 AND \"Url\" IS NULL) OR (\"SourceType\" = 1 AND \"Url\" IS NOT NULL AND length(trim(\"Url\")) > 0)"));
    }
}
