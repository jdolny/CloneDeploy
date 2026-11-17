using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloneDeploy_Server.Backend.Data.Models;

// Independent of device lifetime: removing a computer must not reset a user's acknowledgement.
public sealed class UserNotificationAcknowledgment
{
    public string NotificationId { get; set; } = "";
    public string UserKey { get; set; } = "";
    public DateTime AcknowledgedAtUtc { get; set; }
    public int AcknowledgedOnDeviceId { get; set; }
}
public sealed class UserNotificationAcknowledgmentConfiguration : IEntityTypeConfiguration<UserNotificationAcknowledgment>
{
    public void Configure(EntityTypeBuilder<UserNotificationAcknowledgment> builder)
    {
        builder.ToTable("UserNotificationAcknowledgments");
        builder.HasKey(x => new { MessageId = x.NotificationId, x.UserKey });
        builder.Property(x => x.NotificationId).HasMaxLength(80);
        builder.Property(x => x.UserKey).HasMaxLength(220);
    }
}
