using System.Text.Json;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent;

internal static class PortalNotice
{
    internal static AgentPortalNotification Create(JsonElement settings, Guid runId, Guid actionId)
    {
        var required = settings.Flag("RequireAcknowledgment");
        var notice = settings.Text("NoticeId");
        var now = DateTimeOffset.UtcNow;
        var expires = settings.TryGetProperty("ExpiresAtUtc", out var value) && value.TryGetDateTimeOffset(out var date)
            ? date : now.AddDays(7);
        return new(required && Guid.TryParse(notice, out var id) ? id.ToString("N") : $"{runId:N}:{actionId:N}",
            now, settings.Text("Title"), (settings.TryGetProperty("Body", out _) ? settings.Text("Body") : settings.Text("Message")), settings.Number("Timeout"))
        {
            RequireAcknowledgment = required,
            RemindAtLogin = required,
            ExpiresAt = required ? expires : null,
            Branding = NotificationBranding.Organization,
            Kind = (NotificationKind)settings.Number("Kind"),
            Position = (NotificationPosition)settings.Number("Position")
        };
    }
}
