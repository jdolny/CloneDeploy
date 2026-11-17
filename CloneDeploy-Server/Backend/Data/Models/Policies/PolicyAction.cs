using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models
{
    public class PolicyAction
    {
        public int Id { get; set; }
        
        public int PolicyId { get; set; }
        
        public Guid ActionGuid { get; set; }
        
        public ActionType ActionType { get; set; }
        
        public int Order { get; set; }
        
        public int? ConditionId { get; set; }
        
        public PolicyConditionFailedAction ConditionFailedAction { get; set; }
        
        public Guid? ConditionNextAction { get; set; }
        
        //navigation properties
        public Policy? Policy { get; set; }
        public ActionReference? ActionReference { get; set; }
        public ScriptAction? Condition { get; set; }
        public ActionReference? ConditionNextActionReference { get; set; }
    }
    
    public class PolicyActionConfiguration : IEntityTypeConfiguration<PolicyAction>
    {
        public void Configure(EntityTypeBuilder<PolicyAction> builder)
        {
            builder
                .HasOne(x => x.ActionReference)
                .WithMany(x => x.PolicyActions)
                .HasForeignKey(x => x.ActionGuid)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
