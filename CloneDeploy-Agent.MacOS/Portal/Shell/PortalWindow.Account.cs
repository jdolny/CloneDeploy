using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CloneDeploy_Agent.Contracts;
namespace CloneDeploy_Agent.MacOS.Portal;

public sealed partial class PortalWindow
{
    private readonly TextBlock accountName = Text(PortalText.Get("SignIn.WithYourWorkAccount"), 16, FontWeight.SemiBold);
    private readonly TextBlock accountMessage = Text(PortalText.Get("SignIn.ConnectActiveDirectory"), 13);
    private readonly Button signIn = new() { Content = PortalText.Get("SignIn.SignIn"), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Button signOut = new() { Content = PortalText.Get("SignIn.SignOut"), IsVisible = false };
    private readonly Button cancelSignIn = new() { Content = PortalText.Get("Common.Cancel"), IsVisible = false };
    private CancellationTokenSource? signInStop;
    private AgentPortalUserSession? accountSession;
    private string? accountToken;
    private string? accountKey;
    private bool accountBusy;
    private DateTimeOffset nextAccountCheck;
    private Control BuildAccountPanel()
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(Text(PortalText.Get("Common.WorkAccount"), 18, FontWeight.SemiBold));
        signIn.Classes.Add("primary");
        panel.Children.Add(accountName); panel.Children.Add(accountMessage);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        buttons.Children.Add(signIn); buttons.Children.Add(signOut); buttons.Children.Add(cancelSignIn); panel.Children.Add(buttons);
        signIn.Click += async (_, _) => await SignInAsync();
        signOut.Click += async (_, _) => await SignOutAsync();
        cancelSignIn.Click += (_, _) => signInStop?.Cancel();
        return panel;
    }
    private void AccountControls()
    {
        signIn.IsEnabled = signOut.IsEnabled = !accountBusy;
        signIn.IsVisible = accountSession is null;
        signOut.IsVisible = accountSession is not null;
        cancelSignIn.IsVisible = signInStop is not null;
    }
    private async Task SignInAsync()
    {
        if (accountBusy) return;
        accountBusy = true; signInStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); AccountControls();
        accountMessage.Text = PortalText.Get("SignIn.CompleteInBrowser");
        try
        {
            var result = await PortalSignInFlow.RunAsync(signInStop.Token);
            accountSession = result.Session!; accountToken = accountSession.Token; accountKey = result.StorageKey!;
            ShowAccount();
            try { await Task.Run(() => PortalKeychain.Save(accountKey, accountToken)); }
            catch (IOException) { accountMessage.Text = PortalText.Get("SignIn.KeychainRequiredToRemember"); }
            nextAccountCheck = DateTimeOffset.UtcNow.AddMinutes(5);
        }
        catch (OperationCanceledException) { accountMessage.Text = PortalText.Get("SignIn.CancelledOrExpiredRetry"); }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
        { accountMessage.Text = PortalText.Get("SignIn.ConnectionFailed"); }
        finally { signInStop.Dispose(); signInStop = null; accountBusy = false; AccountControls(); }
    }
    private async Task RefreshAccountAsync(bool force = false)
    {
        if (accountBusy || !force && DateTimeOffset.UtcNow < nextAccountCheck) return;
        accountBusy = true; AccountControls(); nextAccountCheck = DateTimeOffset.UtcNow.AddMinutes(5);
        try
        {
            var context = await PortalClient.AuthenticateAsync(new("context"), stop.Token);
            if (context.Status != "Ready" || context.StorageKey is null) { accountMessage.Text = PortalText.Get("SignIn.AvailableAfterEnrollment"); return; }
            if (accountKey != context.StorageKey)
            {
                accountSession = null; accountToken = null; accountKey = null; ShowAccount();
                var token = await Task.Run(() => PortalKeychain.Read(context.StorageKey));
                accountKey = context.StorageKey; accountToken = token;
            }
            if (accountToken is null) { ShowAccount(); RemindSignIn(); return; }
            var result = await PortalClient.AuthenticateAsync(new("session", Token: accountToken), stop.Token);
            if (result.Status == "SignedOut")
            {
                accountSession = null; accountToken = null; ShowAccount();
                await Task.Run(() => PortalKeychain.Delete(accountKey));
                accountMessage.Text = PortalText.Get("SignIn.SessionEndedReconnect");
                RemindSignIn();
            }
            else if (result.Status == "SignedIn") { accountSession = result.Session; ShowAccount(); }
            else accountMessage.Text = PortalText.Get("SignIn.VerifySessionFailed");
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or OperationCanceledException)
        { accountMessage.Text = PortalText.Get("SignIn.RestoreSessionFailed"); }
        finally { accountBusy = false; AccountControls(); }
    }
    private async Task SignOutAsync()
    {
        if (accountBusy || accountToken is null || accountKey is null) return;
        accountBusy = true; AccountControls();
        try
        {
            var result = await PortalClient.AuthenticateAsync(new("sign-out", Token: accountToken), stop.Token);
            if (result.Status != "SignedOut") throw new IOException();
            accountSession = null; accountToken = null; ShowAccount();
            await Task.Run(() => PortalKeychain.Delete(accountKey));
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or OperationCanceledException)
        { accountMessage.Text = accountSession is null ? PortalText.Get("SignIn.SessionRevokedKeychainEntryRetained") : PortalText.Get("SignIn.SignOutFailed"); }
        finally { accountBusy = false; AccountControls(); }
    }
    private void RemindSignIn()
    {
        signInBanner.IsVisible = accountSession is null;
        if (!promptForMacSignIn)
        {
            signInReminder?.Close();
            signInReminder = null;
            remindedSignIn = false;
            return;
        }
        if (accountSession is not null || remindedSignIn || stop.IsCancellationRequested) return;
        remindedSignIn = true;
        var popup = new Window { Title = PortalText.Get("Common.WorkAccountSignIn"), Width = 380, SizeToContent = SizeToContent.Height,
            CanResize = false, ShowActivated = false, ShowInTaskbar = false, Topmost = true };
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(18) };
        panel.Children.Add(PortalPopupBranding.Header(popup, PortalText.Get("SignIn.WorkAccountHeading")));
        panel.Children.Add(Text(PortalText.Get("SignIn.ConnectWorkAccountDescription"), 13));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var open = new Button { Content = PortalText.Get("Portal.OpenLowercase"), Classes = { "primary" } };
        var later = new Button { Content = PortalText.Get("Common.Later") };
        open.Click += (_, _) => { popup.Close(); openAccount?.Invoke(); };
        later.Click += (_, _) => popup.Close();
        buttons.Children.Add(open); buttons.Children.Add(later); panel.Children.Add(buttons); popup.Content = panel;
        signInReminder = popup; popup.Closed += (_, _) => { if (signInReminder == popup) signInReminder = null; };
        PortalPopupPlacement.Configure(popup, this);
        popup.Show();
    }
    private void ShowAccount()
    {
        selfServicePanel.SetContext(selfServiceEnabled, accountToken);
        userLabel.Text = string.IsNullOrWhiteSpace(accountSession?.User.Name) ? Environment.UserName : accountSession.User.Name;
        signInBanner.IsVisible = accountSession is null;
        notificationsPanel.SetAccount(accountSession is null ? null : accountToken);
        if (accountSession is not null) { signInReminder?.Close(); signInReminder = null; remindedSignIn = false; }
        UpdateHelpContext();
        accountName.Text = accountSession?.User.Name ?? PortalText.Get("SignIn.WithYourWorkAccount");
        accountMessage.Text = accountSession is null ? PortalText.Get("SignIn.ConnectActiveDirectory")
            : PortalText.Format("SignIn.UserSignedIn", accountSession.User.UserPrincipalName ?? accountSession.User.DirectoryName);
    }
}
