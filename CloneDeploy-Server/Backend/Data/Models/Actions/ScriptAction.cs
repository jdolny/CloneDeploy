using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models
{
    public class ScriptAction
    {
        public int Id { get; set; }

        public Guid Guid { get; set; } = Guid.NewGuid();
        
        public required string Name { get; set; }
        
        public string? Description { get; set; }
        
        public ActionScriptType ScriptType { get; set; }
        
        public string? Arguments { get; set; }
        
        public string? ScriptContents { get; set; }
        
        public int Timeout { get; set; }
        
        public string? WorkingDirectory { get; set; }
        
        public bool RedirectStdOut { get; set; }
        
        public bool RedirectStdError { get; set; }
        
        public required string SuccessCodes { get; set; }
        
        public bool AddInventoryCollection { get; set; }
        
        public bool IsCondition { get; set; }

        public int? ManagedCredentialId { get; set; }

        //navigation properties
        public ActionReference? ActionReference { get; set; }
        public ManagedCredential? ManagedCredential { get; set; }
    }
    
    public class ScriptActionConfiguration : IEntityTypeConfiguration<ScriptAction>
    {
        public void Configure(EntityTypeBuilder<ScriptAction> builder)
        {
            builder
                .HasOne(x => x.ActionReference)
                .WithOne()
                .HasPrincipalKey<ActionReference>(x => x.ActionGuid)
                .HasForeignKey<ScriptAction>(x => x.Guid)
                .OnDelete(DeleteBehavior.Cascade);
            
        }
    }
}