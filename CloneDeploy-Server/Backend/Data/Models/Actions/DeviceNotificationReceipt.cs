using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

public sealed class DeviceNotificationReceipt
{
    public int DeviceId { get; set; }
    public string NotificationId { get; set; } = "";
    public string UserSid { get; set; } = "";
    public string UserKey { get; set; } = "";
    public DateTime? ExpiresAtUtc { get; set; }
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public bool AcknowledgedElsewhere { get; set; }
    public string UserName { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTime DeliveredAtUtc { get; set; }
    public DateTime? AcknowledgedAtUtc { get; set; }
}
public sealed class DeviceNotificationReceiptConfiguration : IEntityTypeConfiguration<DeviceNotificationReceipt>
{
    public void Configure(EntityTypeBuilder<DeviceNotificationReceipt> builder)
    {
        builder.HasKey(x => new { x.DeviceId, MessageId = x.NotificationId, x.UserSid });
        builder.Property(x => x.NotificationId).HasMaxLength(80);
        builder.Property(x => x.UserSid).HasMaxLength(184);
        builder.Property(x => x.UserKey).HasMaxLength(220);
        builder.Property(x => x.UserName).HasMaxLength(256);
        builder.Property(x => x.Title).HasMaxLength(200);
        builder.Property(x => x.Body).HasMaxLength(2000);
        builder.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
    }
}
