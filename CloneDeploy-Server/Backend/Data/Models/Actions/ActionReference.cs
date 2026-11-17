using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models
{
    public class ActionReference
    {
        public Guid ActionGuid { get; set; }
        
        public ActionType ActionType { get; set; }
        public DateTime? ArchivedAtUtc { get; set; }
        public int? ExternalFileUpdateScheduleId { get; set; }
        public DateTime? ExternalFilesLastEvaluatedAtUtc { get; set; }
        public Schedule? ExternalFileUpdateSchedule { get; set; }
        
        //navigation properties
        
        public ICollection<ActionTag> Tags { get; set; } = [];
        public ICollection<ActionFile> Files { get; set; } = [];
        public ICollection<PolicyAction> PolicyActions { get; set; } = [];
    }
    
    public class ActionReferenceConfiguration : IEntityTypeConfiguration<ActionReference>
    {
        public void Configure(EntityTypeBuilder<ActionReference> builder)
        {
            builder.HasKey(x => x.ActionGuid);
            builder.HasOne(x => x.ExternalFileUpdateSchedule).WithMany()
                .HasForeignKey(x => x.ExternalFileUpdateScheduleId).OnDelete(DeleteBehavior.Restrict);
        }
    }
}
