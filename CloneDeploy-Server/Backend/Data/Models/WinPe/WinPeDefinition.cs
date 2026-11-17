using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

public sealed class WinPeDefinition
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool IsBase { get; set; }
    public bool IsDefault { get; set; }
    public bool IsArm64Default { get; set; }
    public bool NativePxe { get; set; }

    // Legacy database name: this is the automatically replaced current build, not a publication choice.
    public Guid? PublishedBuildId { get; set; }
    public WinPeBuild? PublishedBuild { get; set; }
    public bool IncludePowerShell { get; set; } = true;
    public bool UseBootEx { get; set; } = true;
    public string KeyboardLayout { get; set; } = WinPe.WinPeKeyboardLayouts.Default;
    public string DriverPackIdsJson { get; set; } = "[]";
    public string ModelRulesJson { get; set; } = "[]";
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class WinPeBuild
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int DefinitionId { get; set; }
    public WinPeDefinition Definition { get; set; } = null!;
    public int Number { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = "";
    public string RequestJson { get; set; } = "";
    public int? StorageProviderId { get; set; }
    public DateTime? StoredAtUtc { get; set; }
    public string StoredArtifactsJson { get; set; } = "[]";
    public string? StorageError { get; set; }
}

public sealed class WinPeDefinitionConfiguration : IEntityTypeConfiguration<WinPeDefinition>
{
    public void Configure(EntityTypeBuilder<WinPeDefinition> b)
    {
        b.Property(x => x.Name).HasColumnType("citext").HasMaxLength(200);
        b.Property(x => x.ModelRulesJson).HasDefaultValue("[]");
        b.Property(x => x.KeyboardLayout).HasMaxLength(13).HasDefaultValue(WinPe.WinPeKeyboardLayouts.Default);
        b.HasIndex(x => x.Name).IsUnique();
        b.HasIndex(x => x.IsDefault).IsUnique().HasFilter("\"IsDefault\" = TRUE");
        b.HasIndex(x => x.IsArm64Default).IsUnique().HasFilter("\"IsArm64Default\" = TRUE");
        b.HasOne(x => x.PublishedBuild).WithMany().HasForeignKey(x => x.PublishedBuildId).OnDelete(DeleteBehavior.Restrict);
    }
}
public sealed class WinPeBuildConfiguration : IEntityTypeConfiguration<WinPeBuild>
{
    public void Configure(EntityTypeBuilder<WinPeBuild> b)
    {
        b.HasOne(x => x.Definition).WithMany().HasForeignKey(x => x.DefinitionId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.DefinitionId, x.Number }).IsUnique();
        b.HasOne<StorageProvider>().WithMany().HasForeignKey(x => x.StorageProviderId).OnDelete(DeleteBehavior.Restrict);
    }
}
