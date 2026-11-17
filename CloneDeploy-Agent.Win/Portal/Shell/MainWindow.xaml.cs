using System.Security.Principal;
using CloneDeploy_Agent.Contracts;
using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CloneDeploy_Agent.UI;

/// <summary>Displays the self-service categories and device policy metadata for the current Windows user.</summary>
public partial class MainWindow : Window
{
    private sealed class AssignedPolicyRow(AgentPortalPolicy policy)
    {
        public string Name => policy.Name;
        public int Priority => policy.Priority;
        public string LastStarted => policy.LastRun?.StartedAtUtc.ToLocalTime().ToString("g") ?? "—";
        public string Result => policy.LastRun is not { } run ? PortalText.Get("Policies.NoReportedRun") : run.FinishedAtUtc is null ? PortalText.Get("Common.ResultPending") :
            run.Result switch { 0 => PortalText.Get("Common.Completed"), 1 => PortalText.Get("Common.Failed"), 2 => PortalText.Get("Common.Skipped"), 3 => PortalText.Get("Common.NotApplicable"), _ => PortalText.Get("Common.UnknownResult") };
    }
    private AgentFeatures features = new();
    internal void ApplyFeatures(AgentFeatures value)
    {
        var refreshCatalog = !features.SelfServiceEnabled && value.SelfServiceEnabled;
        features = value;
        BuildNavigation();
        if (refreshCatalog && IsLoaded) RefreshOnOpen();
    }
    private bool runningPolicy;
    private int? runningPolicyId;
    private readonly Dictionary<string, BitmapSource> iconCache = [];
    private List<PolicyCard> cards = [];
    private sealed class PolicyCard(AgentPortalPolicy policy) : INotifyPropertyChanged
    {
        public AgentPortalPolicy Policy { get; } = policy;
        public string Name => Policy.Name;
        public string? Description => Policy.Description;
        public string Glyph => CategoryGlyph(Policy.Icon ?? Policy.Category?.Icon ?? "Tools");
        private string? statusOverride;
        public string? StatusOverride { get => statusOverride; set { statusOverride = value; Changed(nameof(Status)); } }
        public string Status => StatusOverride ?? (Policy.LastRun is not { } run ? PortalText.Get("Policies.NotRunYet") :
            run.FinishedAtUtc is null ? PortalText.Format("Policies.StartedResultPending", run.StartedAtUtc.ToLocalTime()) :
            PortalText.Format("Common.LabelAndTimestamp", (run.Result switch { 0 => PortalText.Get("Common.Completed"), 1 => PortalText.Get("Common.Failed"), 2 => PortalText.Get("Common.Skipped"), 3 => PortalText.Get("Common.NotApplicable"), _ => PortalText.Get("Common.Finished") }), run.FinishedAtUtc.Value.ToLocalTime()));
        private BitmapSource? image;
        public BitmapSource? Image { get => image; set { image = value; Changed(nameof(Image)); Changed(nameof(HasImage)); } }
        public bool HasImage => Image is not null;
        private bool confirming;
        public bool Confirming
        {
            get => confirming;
            set { confirming = value; Changed(nameof(ConfirmationVisibility)); Changed(nameof(DetailsVisibility)); }
        }
        public Visibility ConfirmationVisibility => Confirming ? Visibility.Visible : Visibility.Collapsed;
        public Visibility DetailsVisibility => Confirming ? Visibility.Collapsed : Visibility.Visible;
        private bool running;
        public bool Running { get => running; set { running = value; Changed(nameof(ButtonText)); } }
        public string ButtonText => Running ? PortalText.Get("Common.Running") : PortalText.Get("SelfService.Run");
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
    }
    private sealed record CatalogCategory(string Key, string Name, int? Id = null);
    private void SearchChanged(object sender, TextChangedEventArgs e) => FilterCards();
    private void CategoryChanged(object sender, SelectionChangedEventArgs e) => FilterCards();
    private void FilterCards()
    {
        if (Navigation.SelectedItem is not PortalPage page || AvailablePolicies is null) return;
        foreach (var card in cards) card.Confirming = false;
        var search = PolicySearch.Text.Trim();
        var category = PolicyCategory.SelectedItem as CatalogCategory;
        var filtered = cards.Where(c => page.IsCategory && (category is null || category.Key == "all" || c.Policy.Category?.Id == category.Id) &&
            (search.Length == 0 || c.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
             c.Description?.Contains(search, StringComparison.CurrentCultureIgnoreCase) == true)).ToArray();
        AvailablePolicies.ItemsSource = filtered;
        CatalogEmpty.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private async Task LoadIconsAsync()
    {
        foreach (var card in cards.ToArray())
        {
            if (card.Policy.IconHash is not { } hash) continue;
            try
            {
                if (!iconCache.TryGetValue(hash, out var bitmap))
                {
                    var bytes = await PortalClient.ReadIconAsync(card.Policy.Id, stop.Token);
                    if (bytes.Length == 0 || !Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).Equals(hash, StringComparison.OrdinalIgnoreCase)) continue;
                    using var stream = new System.IO.MemoryStream(bytes);
                    var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    bitmap = decoder.Frames[0];
                    if (bitmap.PixelWidth > 512 || bitmap.PixelHeight > 512) continue;
                    bitmap.Freeze();
                    if (iconCache.Count >= 64) iconCache.Clear();
                    iconCache[hash] = bitmap;
                }
                card.Image = bitmap;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or OperationCanceledException or
                System.Text.Json.JsonException or NotSupportedException or System.IO.FileFormatException or ArgumentException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
            { /* Keep the built-in fallback when artwork cannot be loaded. */ }
        }
    }
    private bool refreshing;
    private AgentPortalPolicy[] available = [];
    private sealed record PortalPage(string Key, string Title, string Glyph, bool IsCategory = false, int? CategoryId = null);
    private void BuildNavigation()
    {
        var selected = AdminPanel.Visibility == Visibility.Visible ? "admin" : (Navigation.SelectedItem as PortalPage)?.Key;
        var catalog = features.SelfServiceEnabled ? available : [];
        var selectedCategory = (PolicyCategory.SelectedItem as CatalogCategory)?.Key;
        var categories = catalog.Where(p => p.Category is not null).Select(p => p.Category!)
            .DistinctBy(c => c.Id).OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ThenBy(c => c.Id)
            .Select(c => new CatalogCategory($"category:{c.Id}", c.Name, c.Id)).ToList();
        categories.Insert(0, new("all", PortalText.Get("Common.AllCategories")));
        if (catalog.Any(p => p.Category is null)) categories.Add(new("other", PortalText.Get("Common.Other")));
        PolicyCategory.ItemsSource = categories;
        PolicyCategory.SelectedItem = categories.FirstOrDefault(c => c.Key == selectedCategory) ?? categories[0];
        var pages = new List<PortalPage>();
        pages.Add(new("device", PortalText.Get("Common.Device"), "\uE770"));
        if (features.SelfServiceEnabled) pages.Add(new("self-service", PortalText.Get("Common.SelfService"), "\uE71D", true));
        pages.Add(new("notifications", PortalText.Get("Common.Notifications"), "\uE7F4"));
        var unreadHelp = (Application.Current as App)?.HelpNotifications.UnreadCount ?? 0;
        if (features.HelpDeskEnabled) pages.Add(new("help", unreadHelp > 0 ? $"{currentBranding.HelpTabText} ({unreadHelp})" : currentBranding.HelpTabText, "\uE8F2"));
        Navigation.ItemsSource = pages;
        if (selected == "admin") ShowPage(new("admin", PortalText.Get("Common.AdminTools"), "\uE72E"));
        else Navigation.SelectedItem = pages.FirstOrDefault(p => p.Key == selected) ?? pages[0];
        // Equal selections may not raise SelectionChanged after a catalog refresh.
        FilterCards();
    }
    private static string CategoryGlyph(string icon) => icon switch
    {
        "Software" => "\uE71D", "Printers" => "\uE749", "Maintenance" => "\uE90F",
        "Network" => "\uE774", "Security" => "\uE72E", "Help" => "\uE8F2",
        "Productivity" => "\uE8A5", "Communication" => "\uE8F2", "Files & Storage" => "\uE8B7",
        "Updates" => "\uE777", "Accounts" => "\uE77B", "Education" => "\uE7BE", _ => "\uE713"
    };
    private readonly CancellationTokenSource stop = new();

    public MainWindow()
    {
        InitializeComponent();
        InitializeScreenPlacement();
        PortalTheme.Attach(this, () => ApplyBranding(currentBranding));
        if (Application.Current is App app)
        {
            NotificationsList.ItemsSource = app.Notifications.History;
            features = app.Notifications.Features;
            app.Notifications.FeaturesChanged += ApplyFeatures;
            Closed += (_, _) => app.Notifications.FeaturesChanged -= ApplyFeatures;
            ApplyBranding(app.Notifications.Branding);
            app.Notifications.BrandingChanged += ApplyBranding;
            Closed += (_, _) => app.Notifications.BrandingChanged -= ApplyBranding;
            app.HelpNotifications.Changed += BuildNavigation;
            Closed += (_, _) => app.HelpNotifications.Changed -= BuildNavigation;
        }
        BuildNavigation();
        using var user = WindowsIdentity.GetCurrent();
        IdentityText.Text = user.Name;
        DeviceIdentityText.Text = Environment.MachineName;
        Closed += (_, _) => stop.Cancel();
        InitializeAdminSession();
    }
    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        FitToScreen();
        await RefreshAsync();
    }
    private async void RefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();
    internal async void RefreshOnOpen() => await RefreshAsync();
    private async void AcknowledgeNotification(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { DataContext: AgentPortalNotification message } button || Application.Current is not App app) return;
        button.IsEnabled = false;
        try { await app.Notifications.AcknowledgeAsync(message.Id); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or OperationCanceledException or System.Text.Json.JsonException or TimeoutException or System.ComponentModel.Win32Exception)
        { MessageBox.Show(this, PortalText.Get("Notifications.SaveAcknowledgmentFailed"), "CloneDeploy"); }
        finally { button.IsEnabled = true; }
    }
    internal void ShowNotifications() => Navigation.SelectedItem = Navigation.Items.Cast<PortalPage>().First(p => p.Key == "notifications");
    internal void ShowHelp() => Navigation.SelectedItem = Navigation.Items.Cast<PortalPage>().FirstOrDefault(p => p.Key == "help");

    private void NavigationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Navigation.SelectedItem is not PortalPage page) return;
        ShowPage(page);
    }
    private void AdminToolsClick(object sender, RoutedEventArgs e)
    {
        Navigation.SelectedItem = null;
        ShowPage(new("admin", PortalText.Get("Common.AdminTools"), "\uE72E"));
    }
    private void ShowPage(PortalPage page)
    {
        PageTitle.Text = page.Title;
        PageHeader.Visibility = page.Key == "help" ? Visibility.Collapsed : Visibility.Visible;
        SelfServicePanel.Visibility = page.IsCategory ? Visibility.Visible : Visibility.Collapsed;
        NotificationsPanel.Visibility = page.Key == "notifications" ? Visibility.Visible : Visibility.Collapsed;
        AdminPanel.Visibility = page.Key == "admin" ? Visibility.Visible : Visibility.Collapsed;
        DevicePanel.Visibility = page.Key == "device" ? Visibility.Visible : Visibility.Collapsed;
        HelpPanel.Visibility = page.Key == "help" ? Visibility.Visible : Visibility.Collapsed;
        FilterCards();
        PageDescription.Visibility = page.Key == "help" ? Visibility.Collapsed : Visibility.Visible;
        PageDescription.Text = page.IsCategory
            ? PortalText.Get("SelfService.AvailableDescription")
            : page.Key == "help" ? ""
            : page.Key == "device" ? PortalText.Get("Device.AccountDescription")
            : page.Key == "admin" ? PortalText.Get("Admin.SignInDescription")
            : page.Key == "notifications"
                ? PortalText.Get("Notifications.HistoryDescription")
                : PortalText.Get("Policies.AssignedDescription");
    }
    private void RunPolicyClick(object sender, RoutedEventArgs e)
    {
        if (!features.SelfServiceEnabled || runningPolicy || sender is not Button { Tag: PolicyCard card }) return;
        foreach (var item in cards) item.Confirming = item == card;
    }
    private void CancelPolicyClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PolicyCard card }) card.Confirming = false;
    }
    private async void ConfirmPolicyClick(object sender, RoutedEventArgs e)
    {
        if (!features.SelfServiceEnabled || runningPolicy || sender is not Button { Tag: PolicyCard { Confirming: true } card } || !cards.Contains(card)) return;
        var policy = card.Policy;
        card.Confirming = false;
        runningPolicy = true; runningPolicyId = policy.Id; card.Running = true; AvailablePolicies.IsEnabled = false;
        SelfServiceStatus.Text = PortalText.Format("SelfService.RunningPolicy", policy.Name);
        try
        {
            await LoginPolicyClient.RunRequestedAsync(policy.Id, message =>
            {
                if (Application.Current is App app) app.QueuePolicyNotification(message);
            }, stop.Token);
            foreach (var item in cards.Where(c => c.Policy.Id == policy.Id)) item.StatusOverride = PortalText.Format("SelfService.CompletedAt", DateTime.Now);
            SelfServiceStatus.Text = PortalText.Format("SelfService.PolicyCompleted", policy.Name);
        }
        catch (Exception ex)
        {
            foreach (var item in cards.Where(c => c.Policy.Id == policy.Id)) item.StatusOverride = PortalText.Get("SelfService.CompletionUnconfirmed");
            SelfServiceStatus.Text = PortalText.Format("SelfService.CompletionVerificationFailed", ex.Message);
        }
        finally
        {
            runningPolicy = false; runningPolicyId = null; card.Running = false;
            foreach (var item in cards) item.Running = false;
            AvailablePolicies.IsEnabled = true;
        }
    }

    private async Task RefreshAsync()
    {
        if (refreshing || stop.IsCancellationRequested) return;
        refreshing = true;
        RefreshButton.IsEnabled = false;
        StatusText.Text = PortalText.Get("Connection.ConnectingToLocalAgent");
        AgentVersionText.Text = PortalText.Get("Common.AgentVersionConnecting");
        AgentVersionText.ToolTip = PortalText.Get("Device.AgentVersionDescription");
        PoliciesGrid.ItemsSource = null;
        available = [];
        cards = [];
        AvailablePolicies.ItemsSource = null;
        EmptyText.Visibility = Visibility.Collapsed;
        try
        {
            var snapshot = await PortalClient.ReadAsync(stop.Token);
            AgentVersionText.Text = string.IsNullOrWhiteSpace(snapshot.AgentVersion) ? PortalText.Get("Common.AgentVersionNotReported") : PortalText.Format("Device.AgentVersion", AgentUpdateOffer.DisplayVersion(snapshot.AgentVersion));
            if (string.IsNullOrWhiteSpace(snapshot.AgentVersion)) AgentVersionText.ToolTip = PortalText.Get("Device.AgentVersionNotReportedHint");
            IdentityText.Text = snapshot.DirectoryIdentity?.User?.Name ?? snapshot.UserName;
            LocalUserText.Text = snapshot.UserName;
            await RefreshWorkAccountAsync();
            DeviceIdentityText.Text = snapshot.DeviceName;
            DeviceDetailsText.Text = $"{snapshot.DeviceName}\n{snapshot.ServerUrl}";
            IdentityText.ToolTip = snapshot.DirectoryIdentity switch
            {
                { Status: "Matched", User: { } user } => PortalText.Format("SignIn.DirectoryUserDetails", user.Name, user.UserPrincipalName, user.DirectoryName),
                { Status: "Disabled" } => PortalText.Get("SignIn.DirectoryAccountDisabled"),
                { Status: "Missing" } => PortalText.Get("SignIn.DirectoryAccountMissing"),
                { Status: "Ambiguous" } => PortalText.Get("SignIn.DirectoryAccountAmbiguous"),
                { Status: "NotFound" } => PortalText.Get("SignIn.WindowsAccountNotLinked"),
                _ => null
            };
            PoliciesGrid.ItemsSource = snapshot.Policies.Select(p => new AssignedPolicyRow(p)).ToArray();
            available = snapshot.AvailablePolicies;
            cards = available.Select(p => new PolicyCard(p) { Running = p.Id == runningPolicyId }).ToList();
            BuildNavigation();
            if (!runningPolicy) SelfServiceStatus.Text = snapshot.AvailablePolicies.Length == 0 ? PortalText.Get("SelfService.NoAvailablePolicies") : PortalText.Get("SelfService.SelectRunHint");
            EmptyText.Visibility = snapshot.Error is null && snapshot.Policies.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = snapshot.Error ?? PortalText.Format("Portal.UpdatedAt", DateTime.Now);
            await LoadIconsAsync();
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) when (ex is System.IO.IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
        {
            AgentVersionText.Text = PortalText.Get("Common.AgentVersionUnavailable");
            AgentVersionText.ToolTip = PortalText.Get("Connection.AgentServiceUnavailable");
            StatusText.Text = ex is OperationCanceledException
                ? PortalText.Get("Connection.AgentResponseTimeout")
                : PortalText.Format("Portal.LoadFailed", ex.Message);
        }
        finally
        {
            if (available.Length == 0) BuildNavigation();
            refreshing = false;
            RefreshButton.IsEnabled = true;
        }
    }
}
