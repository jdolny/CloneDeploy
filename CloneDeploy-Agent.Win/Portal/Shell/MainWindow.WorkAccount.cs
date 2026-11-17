using System.Windows;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent.UI;

public partial class MainWindow
{
    private CancellationTokenSource? workSignIn;
    private string? displayedWorkAccount;
    private async Task RefreshWorkAccountAsync()
    {
        try
        {
            var account = await PortalClient.WorkAccountAsync(new("status"), stop.Token);
            var key = account.User is { } user ? $"{user.DirectorySourceId}/{user.ObjectGuid}" : "local";
            if (displayedWorkAccount != key)
            {
                displayedWorkAccount = key;
                ClearWorkAccountViews();
            }
            WorkAccountName.Text = account.User?.Name ?? PortalText.Get("SignIn.NoWorkAccount");
            IdentityText.Text = account.User?.Name ?? LocalUserText.Text;
            WorkAccountMessage.Text = account.Status switch
            {
                "Automatic" => PortalText.Get("SignIn.WindowsAccountLinked"),
                "SignedIn" => account.User?.UserPrincipalName ?? PortalText.Get("SignIn.OrganizationConnected"),
                "SignedOut" => PortalText.Get("SignIn.WindowsAccountNotMatched"),
                _ => PortalText.Get("SignIn.WorkAccountUnavailable")
            };
            WorkSignIn.Visibility = account.Status == "SignedOut" ? Visibility.Visible : Visibility.Collapsed;
            WorkSignOut.Visibility = account.Status == "SignedIn" ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is System.IO.IOException or OperationCanceledException or System.Text.Json.JsonException)
        { WorkAccountMessage.Text = PortalText.Get("SignIn.AccountCheckFailed"); WorkSignIn.Visibility = WorkSignOut.Visibility = Visibility.Collapsed; }
    }
    private async void WorkSignInClick(object sender, RoutedEventArgs e)
    {
        if (workSignIn is not null) { workSignIn.Cancel(); return; }
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        workSignIn = pending; WorkSignIn.Content = PortalText.Get("Common.CancelSignIn");
        WorkAccountMessage.Text = PortalText.Get("SignIn.CompleteInBrowser");
        try
        {
            await WorkAccountSignInFlow.RunAsync(pending.Token);
            await RefreshAsync();
            _ = LoginPolicyClient.RunAsync(message => { if (Application.Current is App app) app.QueuePolicyNotification(message); }, stop.Token);
        }
        catch (OperationCanceledException) { WorkAccountMessage.Text = PortalText.Get("SignIn.CancelledOrExpired"); }
        catch (Exception ex) when (ex is System.IO.IOException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
        { WorkAccountMessage.Text = PortalText.Get("SignIn.IncompleteRetry"); }
        finally { workSignIn = null; WorkSignIn.Content = PortalText.Get("SignIn.WithWorkAccount"); }
    }
    private async void WorkSignOutClick(object sender, RoutedEventArgs e)
    {
        WorkSignOut.IsEnabled = false;
        ClearWorkAccountViews();
        IdentityText.Text = LocalUserText.Text;
        try { await PortalClient.WorkAccountAsync(new("sign-out"), stop.Token); await RefreshAsync(); }
        catch (Exception ex) when (ex is System.IO.IOException or OperationCanceledException or System.Text.Json.JsonException)
        { WorkAccountMessage.Text = PortalText.Get("SignIn.SignOutIncomplete"); }
        finally { WorkSignOut.IsEnabled = true; }
    }
    private void ClearWorkAccountViews()
    {
        HelpPanel.ResetWorkAccount();
        if (Application.Current is App app)
        {
            app.Notifications.ResetWorkAccount();
            app.HelpNotifications.SetEnabled(false);
            app.HelpNotifications.SetEnabled(features.HelpDeskEnabled);
        }
    }
}
