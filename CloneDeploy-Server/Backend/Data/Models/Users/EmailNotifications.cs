using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace CloneDeploy_Server.Backend.Data.Models;

public sealed class EmailServerSettings
{
    public int Id { get; set; } = 1;
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public bool StartTls { get; set; } = true;
    public string UserName { get; set; } = "";
    public string PasswordEncrypted { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "CloneDeploy";
    public Guid Revision { get; set; }
}
public sealed class UserEmailSubscription
{
    public int AppUserId { get; set; }
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public string Address { get; set; } = "";
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public bool AdminDigestsAvailable { get; set; }
    public bool Daily { get; set; }
    public bool Weekly { get; set; }
    public bool SkipEmpty { get; set; } = true;
    public bool Approvals { get; set; } = true;
    public bool Imaging { get; set; } = true;
    public bool Policies { get; set; } = true;
    public bool Actions { get; set; } = true;
    public bool DirectorySync { get; set; } = true;
    public bool DeviceWarnings { get; set; } = true;
    public bool ServerUpdates { get; set; }
    public bool HelpDesk { get; set; }
    public bool Security { get; set; }
    public string TimeZoneId { get; set; } = "UTC";
    public int DeliveryMinute { get; set; } = 480;
    public DayOfWeek WeeklyDay { get; set; } = DayOfWeek.Monday;
    public DateTime EnabledAtUtc { get; set; }
    public Guid Revision { get; set; }
}
public enum EmailDeliveryStatus { Sending, Sent, Failed, Uncertain, Skipped }
public sealed class EmailDigestDelivery
{
    public long Id { get; set; }
    public int AppUserId { get; set; }
    public string Kind { get; set; } = "";
    public string Period { get; set; } = "";
    public EmailDeliveryStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string? Detail { get; set; }
}
public sealed class EmailServerSettingsConfiguration : IEntityTypeConfiguration<EmailServerSettings>
{
    public void Configure(EntityTypeBuilder<EmailServerSettings> b) { b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever(); b.Property(x => x.Revision).IsConcurrencyToken(); }
}
public sealed class UserEmailSubscriptionConfiguration : IEntityTypeConfiguration<UserEmailSubscription>
{
    public void Configure(EntityTypeBuilder<UserEmailSubscription> b)
    {
        b.HasKey(x => x.AppUserId); b.HasOne<AppUser>().WithMany().HasForeignKey(x => x.AppUserId).OnDelete(DeleteBehavior.Cascade);
        b.Property(x => x.Revision).IsConcurrencyToken();
    }
}
public sealed class EmailDigestDeliveryConfiguration : IEntityTypeConfiguration<EmailDigestDelivery>
{
    public void Configure(EntityTypeBuilder<EmailDigestDelivery> b)
    {
        b.HasKey(x => x.Id); b.HasOne<AppUser>().WithMany().HasForeignKey(x => x.AppUserId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.AppUserId, x.Kind, x.Period }).IsUnique(); b.Property(x => x.Detail).HasMaxLength(500);
    }
}
