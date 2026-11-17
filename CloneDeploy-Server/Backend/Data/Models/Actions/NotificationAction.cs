using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models
{
    public class NotificationAction
    {
        public int Id { get; set; }
        
        public Guid Guid { get; set; } = Guid.NewGuid();
        
        public required string Name { get; set; }
        
        public string? Description { get; set; }
        
        public required string Title { get; set; }
        
        public required string Body { get; set; }
        
        public int Timeout { get; set; }
        public bool RequireAcknowledgment { get; set; }
        public Guid NoticeId { get; set; } = Guid.NewGuid();
        public DateTime ExpiresAtUtc { get; set; } = DateTime.UtcNow.AddDays(7);
        public bool RemindAtLogin { get; set; }
        public CloneDeploy_Agent.Contracts.NotificationPosition Position { get; set; }

        public CloneDeploy_Agent.Contracts.NotificationBranding Branding { get; set; }
        public CloneDeploy_Agent.Contracts.NotificationKind Kind { get; set; }

        //navigation properties
        public ActionReference? ActionReference { get; set; }
    }
    
    public class NotificationActionConfiguration : IEntityTypeConfiguration<NotificationAction>
    {
        public void Configure(EntityTypeBuilder<NotificationAction> builder)
        {
            builder
                .HasOne(x => x.ActionReference)
                .WithOne()
                .HasPrincipalKey<ActionReference>(x => x.ActionGuid)
                .HasForeignKey<NotificationAction>(x => x.Guid)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}