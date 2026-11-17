using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

public sealed class HelpCategory
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class HelpQueue
{
    public const int UnassignedId = -1;
    public const string UnassignedName = "Unassigned";
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool EmailEnabled { get; set; }
    public ICollection<HelpQueueMember> Members { get; set; } = [];
}

public sealed class HelpQueueMember
{
    public int QueueId { get; set; }
    public int UserId { get; set; }
}

public enum HelpRuleStage { Categorization, Assignment }
public enum HelpRuleField { Title, Description, TitleOrDescription, Category, DeviceName, DeviceIp, UserDirectoryGroup, UserDirectoryOu, DeviceDirectoryGroup, DeviceDirectoryOu }
public enum HelpRuleOperator { Contains, StartsWith, Equals, InRange, Regex }
public sealed record HelpRuleCondition(HelpRuleField Field, HelpRuleOperator Operator, string Value);

public sealed class HelpRoutingRule
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public HelpRuleStage Stage { get; set; }
    public int Order { get; set; }
    public bool Enabled { get; set; } = true;
    public bool MatchAll { get; set; } = true;
    public string ConditionsJson { get; set; } = "[]";
    public int? CategoryId { get; set; }
    public int? QueueId { get; set; }
}

public sealed class HelpRoutingConfiguration : IEntityTypeConfiguration<HelpCategory>, IEntityTypeConfiguration<HelpQueue>,
    IEntityTypeConfiguration<HelpQueueMember>, IEntityTypeConfiguration<HelpRoutingRule>
{
    public void Configure(EntityTypeBuilder<HelpCategory> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(100);
        b.HasIndex(x => x.Name).IsUnique();
    }
    public void Configure(EntityTypeBuilder<HelpQueue> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(100);
        b.HasIndex(x => x.Name).IsUnique();
        b.HasData(new HelpQueue { Id = HelpQueue.UnassignedId, Name = HelpQueue.UnassignedName });
        b.HasMany(x => x.Members).WithOne().HasForeignKey(x => x.QueueId).OnDelete(DeleteBehavior.Cascade);
    }
    public void Configure(EntityTypeBuilder<HelpQueueMember> b)
    {
        b.HasKey(x => new { x.QueueId, x.UserId });
        b.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
    public void Configure(EntityTypeBuilder<HelpRoutingRule> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(100);
        b.HasIndex(x => new { x.Stage, x.Order });
        b.HasOne<HelpCategory>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<HelpQueue>().WithMany().HasForeignKey(x => x.QueueId).OnDelete(DeleteBehavior.Restrict);
    }
}
