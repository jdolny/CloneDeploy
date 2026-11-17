using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CloneDeploy_Agent.Contracts;
namespace CloneDeploy_Agent.MacOS.Portal;

internal sealed class PortalSelfServicePanel : UserControl
{
    private readonly Func<MacPortalAuthRequest, Task<MacSelfServiceReply>> request;
    private readonly TextBox search = new() { PlaceholderText = PortalText.Get("SelfService.SearchPlaceholder"), MinWidth = 180 };
    private sealed record CategoryOption(int? Id, string Name, bool All = false)
    {
        public override string ToString() => Name;
    }
    private readonly ComboBox category = new() { MinWidth = 160 };
    private readonly TextBlock message = new() { TextWrapping = TextWrapping.Wrap };
    private readonly WrapPanel cards = new() { Orientation = Orientation.Horizontal, ItemSpacing = 16, LineSpacing = 16 };
    private AgentPortalPolicy[] policies = [];
    private string? token, fingerprint;
    private bool enabled, busy, submitting;
    private Guid pending;
    private int? confirming;
    private readonly Dictionary<string, Bitmap> icons = new(StringComparer.OrdinalIgnoreCase);
    private bool loadingIcons, disposed;
    public PortalSelfServicePanel(Func<MacPortalAuthRequest, Task<MacSelfServiceReply>> request)
    {
        this.request = request;
        var body = new Grid { Margin = new Thickness(24), RowDefinitions = new("Auto,Auto,*") };
        var filters = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new Thickness(0, 0, 0, 12) };
        filters.Children.Add(search); Grid.SetColumn(category, 1); category.Margin = new Thickness(12, 0, 0, 0); filters.Children.Add(category);
        body.Children.Add(filters); Grid.SetRow(message, 1); body.Children.Add(message); message.Margin = new Thickness(0, 0, 0, 12);
        var scroll = new ScrollViewer { Content = cards, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 2); body.Children.Add(scroll); Content = body;
        category.ItemsSource = new[] { new CategoryOption(null, PortalText.Get("Common.AllCategories"), All: true) }; category.SelectedIndex = 0;
        search.TextChanged += (_, _) => Render(); category.SelectionChanged += (_, _) => Render();
    }
    public void SetContext(bool available, string? session)
    {
        if (token == session && enabled == available) return;
        token = session; enabled = available; policies = []; fingerprint = null; pending = Guid.Empty; confirming = null;
        message.Text = !enabled ? PortalText.Get("SelfService.SelfServiceIsDisabledByYourAdministrator") : token is null ? PortalText.Get("SelfService.SignInRequired") : PortalText.Get("SelfService.Loading");
        Render();
        foreach (var bitmap in icons.Values) bitmap.Dispose(); icons.Clear();
    }
    public async Task RefreshAsync()
    {
        if (busy || submitting || !enabled || token is null) return;
        busy = true; var current = token;
        try
        {
            if (pending != Guid.Empty)
            {
                var status = await request(new("self-service-status", Token: current, RequestId: pending));
                if (current != token) return;
                message.Text = status.Message ?? status.Status;
                if (status.Status is not ("Queued" or "Running")) { pending = Guid.Empty; Render(); }
            }
            var result = await request(new("self-service", Token: current));
            if (current != token) return;
            if (result.Status != "Catalog") { message.Text = result.Message ?? PortalText.Get("SelfService.Unavailable"); policies = []; Render(); return; }
            var next = System.Text.Json.JsonSerializer.Serialize(result.Policies);
            if (fingerprint == next) { _ = LoadIconsAsync(current); return; }
            fingerprint = next; policies = result.Policies ?? [];
            var selected = category.SelectedItem as CategoryOption;
            var categories = new[] { new CategoryOption(null, PortalText.Get("Common.AllCategories"), All: true) }
                .Concat(policies.Select(x => new CategoryOption(x.Category?.Id, x.Category?.Name ?? PortalText.Get("Common.Other")))
                    .DistinctBy(x => x.Id).OrderBy(x => x.Name)).ToArray();
            category.ItemsSource = categories; category.SelectedItem = categories.FirstOrDefault(x => x.Id == selected?.Id && x.All == selected?.All) ?? categories[0];
            if (pending == Guid.Empty && message.Text == PortalText.Get("SelfService.Loading")) message.Text = PortalText.Get("SelfService.SelectRunHint");
            Render();
            _ = LoadIconsAsync(current);
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or OperationCanceledException)
        { if (current == token) message.Text = PortalText.Get("SelfService.RefreshFailed"); }
        finally { busy = false; }
    }
    private void Render()
    {
        cards.Children.Clear();
        var visible = policies.Where(x => string.IsNullOrWhiteSpace(search.Text) || (x.Name + " " + x.Description).Contains(search.Text, StringComparison.OrdinalIgnoreCase))
            .Where(x => category.SelectedItem is not CategoryOption selected || selected.All || x.Category?.Id == selected.Id)
            .OrderBy(x => x.Category?.SortOrder ?? int.MaxValue).ThenBy(x => x.Priority).ThenBy(x => x.Name).ToArray();
        foreach (var policy in visible)
        {
            var card = new StackPanel { Spacing = 8 };
            var heading = new Grid { ColumnDefinitions = new("Auto,*") };
            Control artwork = policy.IconHash is { } hash && icons.TryGetValue(hash, out var bitmap)
                ? new Image { Source = bitmap, Width = 36, Height = 36, Stretch = Stretch.Uniform }
                : new PathIcon { Data = Geometry.Parse(IconPath(policy.Icon ?? policy.Category?.Icon)), Width = 28, Height = 28 };
            artwork.Margin = new Thickness(0, 0, 12, 0); heading.Children.Add(artwork);
            var name = new TextBlock { Text = policy.Name, FontSize = 17, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(name, 1); heading.Children.Add(name); card.Children.Add(heading);
            if (!string.IsNullOrWhiteSpace(policy.Description)) card.Children.Add(new TextBlock { Text = policy.Description, TextWrapping = TextWrapping.Wrap });
            if (policy.LastRun is { } last) card.Children.Add(new TextBlock { Text = PortalText.Format("SelfService.LastRun", last.StartedAtUtc.ToLocalTime(), (last.Result is { } result ? PortalText.Get((PolicyRunResult)result switch { PolicyRunResult.Success => "Common.Completed", PolicyRunResult.Failed => "Common.Failed", PolicyRunResult.Skipped => "Common.Skipped", PolicyRunResult.NotApplicable => "Common.NotApplicable", _ => "Common.UnknownResult" }) : PortalText.Get("SelfService.Running"))), FontSize = 12, Classes = { "muted" } });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            var run = new Button { Content = confirming == policy.Id ? PortalText.Get("SelfService.Confirm") : PortalText.Get("SelfService.Run"), IsEnabled = pending == Guid.Empty && enabled && token is not null, Classes = { "primary" } };
            run.Click += async (_, _) =>
            {
                if (confirming != policy.Id) { confirming = policy.Id; Render(); }
                else await RunAsync(policy);
            };
            buttons.Children.Add(run);
            if (confirming == policy.Id)
            {
                card.Children.Add(new TextBlock { Text = PortalText.Format("SelfService.ConfirmRunOnMac", policy.Name), TextWrapping = TextWrapping.Wrap });
                var cancel = new Button { Content = PortalText.Get("Common.Cancel") }; cancel.Click += (_, _) => { confirming = null; Render(); }; buttons.Children.Add(cancel);
            }
            card.Children.Add(buttons); cards.Children.Add(new Border { Classes = { "card" }, Width = 220, MinHeight = 274, Padding = new Thickness(18), VerticalAlignment = VerticalAlignment.Top, Child = card });
        }
        if (visible.Length == 0 && enabled && token is not null) cards.Children.Add(new TextBlock { Text = PortalText.Get("SelfService.NoMatchingPolicies"), TextWrapping = TextWrapping.Wrap });
    }
    private static string IconPath(string? key) => key switch
    {
        "Files & Storage" => "M2,4 H9 L11,6 H22 V20 H2 Z",
        "Security" => "M12,2 L21,6 V12 C21,17 17,21 12,23 C7,21 3,17 3,12 V6 Z",
        "Help" or "Communication" => "M2,3 H22 V18 H9 L3,23 V18 H2 Z",
        "Accounts" => "M12,2 A5,5 0 1 1 12,12 A5,5 0 1 1 12,2 M2,22 C2,12 22,12 22,22 Z",
        "Network" => "M9,1 H15 V7 H13 V11 H21 V16 H23 V22 H17 V16 H19 V13 H5 V16 H7 V22 H1 V16 H3 V11 H11 V7 H9 Z",
        "Maintenance" or "Tools" or "Updates" => "M14,2 C10,2 8,5 9,9 L2,16 L2,20 L6,20 L13,13 C18,14 22,10 21,6 L17,10 L13,6 L17,2 Z",
        _ => "M2,2 H10 V10 H2 Z M14,2 H22 V10 H14 Z M2,14 H10 V22 H2 Z M14,14 H22 V22 H14 Z"
    };
    private async Task LoadIconsAsync(string current)
    {
        if (loadingIcons || disposed || !enabled || token != current) return;
        loadingIcons = true;
        try
        {
            foreach (var policy in policies.Where(x => x.IconHash is not null && !icons.ContainsKey(x.IconHash)).Take(8).ToArray())
            {
                if (disposed || !enabled || token != current || icons.Count >= 64) break;
                try
                {
                    var reply = await request(new("self-service-icon", Token: current, PolicyId: policy.Id));
                    if (disposed || !enabled || token != current) break;
                    var bytes = reply.Icon;
                    if (bytes is not { Length: >= 24 and <= 65536 } || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
                        !Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).Equals(policy.IconHash, StringComparison.OrdinalIgnoreCase)) continue;
                    var width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
                    var height = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
                    if (width is 0 or > 512 || height is 0 or > 512) continue;
                    using var stream = new MemoryStream(bytes);
                    var bitmap = new Bitmap(stream);
                    if (!icons.TryAdd(policy.IconHash!, bitmap)) bitmap.Dispose();
                    Render();
                }
                catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or OperationCanceledException or ArgumentException or NotSupportedException)
                { /* Keep the local fallback if an icon cannot be downloaded or decoded. */ }
            }
        }
        finally { loadingIcons = false; }
    }
    public void Dispose()
    {
        disposed = true; cards.Children.Clear();
        foreach (var bitmap in icons.Values) bitmap.Dispose(); icons.Clear();
    }
    private async Task RunAsync(AgentPortalPolicy policy)
    {
        if (submitting || pending != Guid.Empty || token is null || !enabled) return;
        submitting = true; var current = token; pending = Guid.NewGuid(); confirming = null; Render(); message.Text = PortalText.Format("SelfService.RequestingPolicy", policy.Name);
        try
        {
            var result = await request(new("self-service-run", Token: current, PolicyId: policy.Id, RequestId: pending));
            if (current != token) return;
            message.Text = result.Message ?? result.Status;
            if (result.Status is not ("Queued" or "Running")) pending = Guid.Empty;
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or OperationCanceledException)
        { if (current == token) message.Text = PortalText.Get("SelfService.CheckingRequestAccepted"); }
        finally { submitting = false; Render(); }
    }
}
