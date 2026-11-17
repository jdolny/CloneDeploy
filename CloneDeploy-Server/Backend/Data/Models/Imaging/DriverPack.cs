using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

public class DriverPack
{
    public int Id { get; set; }
    public Guid DirectoryGuid { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;
    public string? ManufacturerRegex { get; set; }
    public string? ModelRegex { get; set; }
    public int? UpdateScheduleId { get; set; }
    public DateTime? LastEvaluatedAtUtc { get; set; }
    public Schedule? UpdateSchedule { get; set; }
    public ICollection<DriverProfileTag> Tags { get; set; } = [];
    public ICollection<DriverModelLink> Models { get; set; } = [];
    public ICollection<DriverPackRegexRule> RegexRules { get; set; } = [];
    public ICollection<DriverFile> Files { get; set; } = [];
}
public class DriverProfileTag
{
    public int DriverProfileId { get; set; }
    public int TagId { get; set; }
    public Tag? Tag { get; set; }
}
public class DriverModelLink
{
    public int Id { get; set; }
    public int DriverProfileId { get; set; }
    public required string Manufacturer { get; set; }
    public required string Model { get; set; }
}
public class DriverFile
{
    public int DriverProfileId { get; set; }
    public int StoredFileId { get; set; }
    public StoredFile File { get; set; } = null!;
    public DriverVendor? CatalogVendor { get; set; }
    public string? CatalogModel { get; set; }
    public string? CatalogOs { get; set; }
    public string? CatalogArchitecture { get; set; }
    public string? CatalogRelease { get; set; }
    public string? CatalogSource { get; set; }
    public bool FollowLatest { get; set; }
    public DriverPreparationStatus PreparationStatus { get; set; }
    public string? AttemptedSourceHash { get; set; }
    public string? PreparationError { get; set; }
    public int? PreparedStorageProviderId { get; set; }
    public Guid? PreparedArchiveId { get; set; }
    public string? PreparedSourceHash { get; set; }
    public string? PreparedSha256 { get; set; }
    public int PreparedInfCount { get; set; }
    public DateTime? PreparedAtUtc { get; set; }

}
public sealed class DriverPackRegexRule
{
    public int Id { get; set; }
    public int DriverPackId { get; set; }
    public string? ManufacturerRegex { get; set; }
    public string? ModelRegex { get; set; }
}
public sealed class DriverPackRegexRuleConfiguration : IEntityTypeConfiguration<DriverPackRegexRule>
{
    public void Configure(EntityTypeBuilder<DriverPackRegexRule> b)
    {
        b.ToTable("DriverPackRegexRules");
        b.Property(x => x.ManufacturerRegex).HasMaxLength(500);
        b.Property(x => x.ModelRegex).HasMaxLength(500);
    }
}
public enum DriverPreparationStatus { Queued, Extracting, Ready, Error }

public sealed class DriverProfileConfiguration : IEntityTypeConfiguration<DriverPack>
{
    public void Configure(EntityTypeBuilder<DriverPack> b)
    {
        b.ToTable("DriverProfiles");
        b.Property(x => x.Name).HasColumnType("citext");
        b.HasIndex(x => x.Name).IsUnique(); b.HasIndex(x => x.DirectoryGuid).IsUnique();
        b.HasMany(x => x.Tags).WithOne().HasForeignKey(x => x.DriverProfileId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Models).WithOne().HasForeignKey(x => x.DriverProfileId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.RegexRules).WithOne().HasForeignKey(x => x.DriverPackId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Files).WithOne().HasForeignKey(x => x.DriverProfileId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.UpdateSchedule).WithMany().HasForeignKey(x => x.UpdateScheduleId).OnDelete(DeleteBehavior.Restrict);
    }
}
public sealed class DriverProfileTagConfiguration : IEntityTypeConfiguration<DriverProfileTag>
{
    public void Configure(EntityTypeBuilder<DriverProfileTag> b)
    {
        b.HasKey(x => new { x.DriverProfileId, x.TagId });
        b.HasOne(x => x.Tag).WithMany().HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Cascade);
    }
}
public sealed class DriverModelLinkConfiguration : IEntityTypeConfiguration<DriverModelLink>
{
    public void Configure(EntityTypeBuilder<DriverModelLink> b)
    {
        b.Property(x => x.Manufacturer).HasColumnType("citext"); b.Property(x => x.Model).HasColumnType("citext");
        b.HasIndex(x => new { x.DriverProfileId, x.Manufacturer, x.Model }).IsUnique();
    }
}
public sealed class DriverFileConfiguration : IEntityTypeConfiguration<DriverFile>
{
    public void Configure(EntityTypeBuilder<DriverFile> b)
    {
        b.HasKey(x => new { x.DriverProfileId, x.StoredFileId });
        b.HasIndex(x => x.StoredFileId).IsUnique();
        b.HasOne<StorageProvider>().WithMany().HasForeignKey(x => x.PreparedStorageProviderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.File).WithMany().HasForeignKey(x => x.StoredFileId).OnDelete(DeleteBehavior.Restrict);
    }
}
