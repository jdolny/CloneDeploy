namespace CloneDeploy_Server.Backend.Data.Models
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.EntityFrameworkCore.Metadata.Builders;

    public class SocketConnection
    {
        public int Id { get; set; }
        
        public int DeviceId { get; set; }
        
        public required string ConnectionId { get; set; }
        public Guid ServerInstanceId { get; set; }
        public Guid? AgentCertificateId { get; set; }
        public AgentCertificate? AgentCertificate { get; set; }
        public DateTime ConnectedAtUtc { get; set; }
        public DateTime LastSeenAtUtc { get; set; }
        
        //navigation properties
        public Device? Device { get; set; }
    }

    public sealed class SocketConnectionConfiguration : IEntityTypeConfiguration<SocketConnection>
    {
        public void Configure(EntityTypeBuilder<SocketConnection> builder)
        {
            builder.Property(x => x.ConnectionId).HasMaxLength(128);
            builder.HasIndex(x => x.ConnectionId).IsUnique();
            builder.HasIndex(x => x.LastSeenAtUtc);
            builder.HasOne(x => x.AgentCertificate).WithMany().HasForeignKey(x => x.AgentCertificateId)
                .OnDelete(DeleteBehavior.SetNull);
        }
    }
}
