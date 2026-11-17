using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models
{
    public class CommandAction
    {
        public int Id { get; set; }
        
        public Guid Guid { get; set; } = Guid.NewGuid();
        
        public required string Name { get; set; }
        
        public string? Description { get; set; }
        
        public required string Command { get; set; }
        
        public string? Arguments { get; set; }
        
        public int Timeout { get; set; }
        
        public string? WorkingDirectory { get; set; }
        
        public bool RedirectStdOut { get; set; }
        
        public bool RedirectStdError { get; set; }
        
        public required string SuccessCodes { get; set; }

        public int? ManagedCredentialId { get; set; }

        // Navigation properties
        public ActionReference? ActionReference { get; set; }
        public ManagedCredential? ManagedCredential { get; set; }
        
    }
    
    public class CommandActionConfiguration : IEntityTypeConfiguration<CommandAction>
    {
        public void Configure(EntityTypeBuilder<CommandAction> builder)
        {
            builder
                .HasOne(x => x.ActionReference)
                .WithOne()
                .HasPrincipalKey<ActionReference>(x => x.ActionGuid)
                .HasForeignKey<CommandAction>(x => x.Guid)
                .OnDelete(DeleteBehavior.Cascade);
            
        }
    }
}