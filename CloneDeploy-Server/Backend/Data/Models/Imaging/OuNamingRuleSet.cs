using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

public enum OuNameMatch { Wildcard, Regex }
public enum OuNoMatch { DomainDefault, SpecificOu, Fail }

public sealed class OuNamingRule
{
    public string Name { get; set; } = "";
    public string Pattern { get; set; } = "";
    public OuNameMatch Match { get; set; }
    public Guid OuGuid { get; set; }
}

public sealed class OuNamingRuleSet
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int? DirectorySourceId { get; set; }
    public string DomainName { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public OuNoMatch Fallback { get; set; } = OuNoMatch.Fail;
    public Guid? FallbackOuGuid { get; set; }
    public string RulesJson { get; set; } = "[]";
    public int Revision { get; set; }
    [NotMapped] public List<OuNamingRule> Rules
    {
        get => JsonSerializer.Deserialize<List<OuNamingRule>>(RulesJson) ?? [];
        set => RulesJson = JsonSerializer.Serialize(value);
    }
}

public sealed class OuNamingRuleSetConfiguration : IEntityTypeConfiguration<OuNamingRuleSet>
{
    public void Configure(EntityTypeBuilder<OuNamingRuleSet> b)
    {
        b.Property(x => x.Name).HasColumnType("citext").HasMaxLength(200);
        b.HasIndex(x => x.Name).IsUnique();
        b.Property(x => x.Description).HasMaxLength(2000);
        b.Property(x => x.DomainName).HasMaxLength(253);
        b.Property(x => x.Revision).IsConcurrencyToken();
        b.HasOne<DirectorySource>().WithMany().HasForeignKey(x => x.DirectorySourceId).OnDelete(DeleteBehavior.Restrict);
    }
}
