using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models
{
    /// <summary>
    /// Represents a windows update found on a computer inventory, linked to a device in the DeviceUpdate table
    /// </summary>
    public class WindowsUpdate
    {
        public int Id { get; set; }
        public int? ApprovedRevisionNumber { get; set; }
        public DateTime? ApprovedAtUtc { get; set; }
        public string? ApprovedBy { get; set; }
        public int? ApprovedByRuleId { get; set; }
        public int? AutoApprovalBlockedRevision { get; set; }
        public DateTime? RevisionFirstSeenAtUtc { get; set; }
        public int? RevisionNumber { get; set; }
        public string[]? KBArticleIds { get; set; }
        public string? CategoriesJson { get; set; }
        public string? MsrcSeverity { get; set; }
        public string? Type { get; set; }
        
        public string? Title { get; set; }
        
        public string? Category { get; set; }
        
        public string? UpdateId { get; set; }
    }

    public sealed class WindowsUpdateConfiguration : IEntityTypeConfiguration<WindowsUpdate>
    {
        public void Configure(EntityTypeBuilder<WindowsUpdate> builder)
        {
            builder.Property(x => x.CategoriesJson).HasColumnType("jsonb");
            builder.HasIndex(x => x.UpdateId).IsUnique().AreNullsDistinct(false);
        }
    }
}
