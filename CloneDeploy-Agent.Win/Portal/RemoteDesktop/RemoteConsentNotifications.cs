using System.IO;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent.UI;

internal static class RemoteConsentNotifications
{
    private const int ActiveSessionNotificationSeconds = 86400;
    private const int EndedSessionNotificationSeconds = 60;
    private const int StatusPollMilliseconds = 1000;

    public static async Task RunAsync(Func<AgentBranding> branding, CancellationToken ct)
    {
        NotificationPopup? window = null;
        (Guid?, bool) shown = (null, false);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var state = await PortalClient.RemoteConsentAsync(new(), ct);
                    var next = (state.SessionId, state.Active);
                    if (shown != next)
                    {
                        window?.CloseFromService(); window = null; shown = next;
                        if (state.SessionId is { } id)
                        {
                            var message = new AgentPortalNotification(id.ToString(), DateTimeOffset.UtcNow,
                                state.Active ? PortalText.Get("Notifications.RemoteSharingIsActive") : PortalText.Get("Notifications.RemoteAccessRequest"),
                                state.Active ? PortalText.Format("RemoteSupport.SharingActiveDescription", state.Requester)
                                    : PortalText.Format("RemoteSupport.ConsentDescription", state.Requester),
                                state.Active ? ActiveSessionNotificationSeconds : EndedSessionNotificationSeconds)
                            { RequireAcknowledgment = true, Position = state.Active ? NotificationPosition.LowerRight : NotificationPosition.Center,
                                Branding = NotificationBranding.Organization, ExpiresAt = state.ExpiresAt };
                            window = new NotificationPopup(message, () => { })
                            {
                                Branding = branding(),
                                ActionFailureMessage = PortalText.Get("RemoteSupport.SendDecisionFailed"),
                                AcknowledgeNotification = async () => { await PortalClient.RemoteConsentAsync(new(id, RemoteConsentAction.Allow), ct); },
                                DismissMessage = async () => { await PortalClient.RemoteConsentAsync(new(id, state.Active ? RemoteConsentAction.Stop : RemoteConsentAction.Deny), ct); }
                            };
                            window.SetConsentButtons(state.Active);
                            window.Show();
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or OperationCanceledException or JsonException or System.ComponentModel.Win32Exception)
                { if (ct.IsCancellationRequested) break; }
                await Task.Delay(StatusPollMilliseconds, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { window?.CloseFromService(); }
    }
}
