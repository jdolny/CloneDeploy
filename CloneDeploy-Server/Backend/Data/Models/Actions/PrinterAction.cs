using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models
{
    public class PrinterAction
    {
        public int Id { get; set; }

        public Guid Guid { get; set; } = Guid.NewGuid();
        
        public required string Name { get; set; }
        
        public string? Description { get; set; }
        
        public required string NetworkPath { get; set; }
        
        public PrinterActionType Action { get; set; }
        
        public bool IsDefault { get; set; }
        
        public bool RestartSpooler { get; set; }

        public bool WaitForEnumeration { get; set; }

        //navigation properties
        public ActionReference? ActionReference { get; set; }
    }
    
    public class PrinterActionConfiguration : IEntityTypeConfiguration<PrinterAction>
    {
        public void Configure(EntityTypeBuilder<PrinterAction> builder)
        {
            builder
                .HasOne(x => x.ActionReference)
                .WithOne()
                .HasPrincipalKey<ActionReference>(x => x.ActionGuid)
                .HasForeignKey<PrinterAction>(x => x.Guid)
                .OnDelete(DeleteBehavior.Cascade);
            
        }
    }
}