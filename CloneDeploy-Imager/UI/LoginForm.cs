using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using CloneDeploy_Imager.Api;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace CloneDeploy_Imager.UI;

/// <summary>
/// Holds credentials entered on the login screen without exposing the password in its string representation.
/// </summary>
internal sealed class LoginCredentials(string userName, string password)
{
    public string UserName { get; } = userName;
    public string Password { get; } = password;
    public override string ToString() => "Login credentials (redacted)";
}

/// <summary>
/// Collects username and password together and displays server connectivity and sign-in errors.
/// </summary>
internal sealed partial class ImagerConsole
{
    public async Task<LoginCredentials?> AskCredentialsAsync(string userName, string? error,
        ImagingClient client, CancellationToken ct)
    {
        if (Console.IsInputRedirected) throw new InvalidOperationException("Interactive input requires a keyboard console.");
        if (plain || !console.Profile.Capabilities.Ansi || !console.Profile.Capabilities.Interactive)
        {
            Heading();
            Line("Sign in");
            Line(LocalNetworkSummary());
            Line("Server: " + client.ServerAddress);
            Line((await client.CheckLoginConnectionAsync(ct)).Message);
            if (error is not null) Line(error);
            var name = await AskAsync("Username:", false, ct);
            if (string.IsNullOrWhiteSpace(name)) return null;
            var password = await AskAsync("Password:", true, ct);
            if (password.Length == 0) return null;
            return new(name.Trim(), password);
        }

        Heading();
        var fields = new[] { new StringBuilder(userName), new StringBuilder() };
        var focus = string.IsNullOrEmpty(userName) ? 0 : 1;
        var network = LocalNetworkSummary();
        LoginConnection? connection = null;
        using var checking = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = client.CheckLoginConnectionAsync(checking.Token);
        var cancelled = false;
        IRenderable Screen()
        {
            var width = Math.Max(20, Math.Min(console.Profile.Width - 4, 72));
            var fieldWidth = Math.Max(8, width - 8);
            var content = new List<IRenderable>
            {
                new Text(ApplicationTitle, new Style(Color.Grey)).Centered(),
                new Text("SIGN IN", new Style(Color.Cyan1, decoration: Decoration.Bold)).Centered(),
                new Text("")
            };
            if (DeviceName is not null) content.Add(new Text("Device Name: " + Clean(DeviceName)).Centered());
            for (var i = 0; i < fields.Length; i++)
            {
                content.Add(new Text(i == 0 ? "Username" : "Password"));
                var visible = i == 1 ? new string('*', fields[i].Length) : Clean(fields[i].ToString());
                if (visible.Length > fieldWidth - 1) visible = visible[^(fieldWidth - 1)..];
                content.Add(new Panel(new Text(visible + (focus == i ? "_" : " "))) { Width = fieldWidth + 2 }
                    .Border(BoxBorder.Rounded).BorderColor(focus == i ? Color.Cyan1 : Color.Grey));
            }
            if (error is not null) content.Add(new Text(Clean(error), new Style(Color.Red)));
            content.Add(new Text(Clean(network), new Style(Color.Grey)));
            content.Add(new Text("Server: " + Clean(client.ServerAddress), new Style(Color.Grey)));
            content.Add(new Text(connection?.Message ?? "Checking server connection...",
                new Style(connection?.Reachable == true ? Color.Green : Color.Yellow)));
            var card = new Panel(new Rows(content)) { Width = width }
                .Border(BoxBorder.Rounded).BorderColor(Color.Cyan1).Padding(2, 1);
            return new Rows(new Align(card, HorizontalAlignment.Center, VerticalAlignment.Middle)
                { Width = console.Profile.Width, Height = Math.Max(1, console.Profile.Height - 3) },
                new Text("Tab: switch field | Enter: next/sign in | F5: test network | Esc: menu", new Style(Color.Grey)).Centered());
        }
        try
        {
            await console.Live(Screen()).AutoClear(true).StartAsync(async context =>
            {
                var dimensions = (console.Profile.Width, console.Profile.Height);
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var redraw = false;
                    if (connection is null && pending.IsCompleted)
                    {
                        connection = await pending;
                        redraw = true;
                    }
                    var current = (console.Profile.Width, console.Profile.Height);
                    if (current != dimensions) { dimensions = current; redraw = true; }
                    if (Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(intercept: true);
                        if (key.Key == ConsoleKey.Escape) { cancelled = true; break; }
                        if (key.Key == ConsoleKey.Tab || key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow) focus = 1 - focus;
                        else if (key.Key == ConsoleKey.F5 && pending.IsCompleted)
                        {
                            network = LocalNetworkSummary();
                            connection = null;
                            pending = client.CheckLoginConnectionAsync(checking.Token);
                        }
                        else if (key.Key == ConsoleKey.Enter)
                        {
                            if (focus == 0) focus = 1;
                            else if (fields[0].ToString().Trim().Length > 0 && fields[1].Length > 0) break;
                            else error = "Enter both a username and password.";
                        }
                        else if (key.Key == ConsoleKey.Backspace && fields[focus].Length > 0) fields[focus].Length--;
                        else if (!char.IsControl(key.KeyChar) && fields[focus].Length < MaximumInputCharacters) fields[focus].Append(key.KeyChar);
                        redraw = true;
                    }
                    if (redraw) { context.UpdateTarget(Screen()); context.Refresh(); }
                    await Task.Delay(30, ct);
                }
            });
            return cancelled ? null : new(fields[0].ToString().Trim(), fields[1].ToString());
        }
        finally
        {
            checking.Cancel();
            try { await pending; }
            catch (OperationCanceledException) when (checking.IsCancellationRequested) { }
            fields[1].Clear();
        }
    }

    internal static string LocalNetworkSummary()
    {
        try
        {
            var adapters = NetworkInterface.GetAllNetworkInterfaces()
                .Where(x => x.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)).ToArray();
            if (adapters.Length == 0) return "No network adapter detected. Check WinPE network drivers.";
            var active = adapters.Where(x => x.OperationalStatus == OperationalStatus.Up).ToArray();
            if (active.Length == 0) return "No active network link. Check the cable, adapter, and driver.";
            var addresses = active.SelectMany(x => x.GetIPProperties().UnicastAddresses)
                .Select(x => x.Address).Where(x => !IPAddress.IsLoopback(x) &&
                    x.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6).Distinct().ToArray();
            if (addresses.Length == 0) return "No IP address assigned. Check DHCP or static network settings.";
            var onlyLinkLocal = addresses.All(x => x.IsIPv6LinkLocal || x.ToString().StartsWith("169.254.", StringComparison.Ordinal));
            return "IP: " + string.Join(", ", addresses.OrderBy(x => x.AddressFamily).Take(4).Select(x => x.ToString())) +
                (onlyLinkLocal ? " (link-local only; check DHCP/static settings)" : "");
        }
        catch (Exception exception) when (exception is NetworkInformationException or PlatformNotSupportedException)
        {
            return "Network adapter information is unavailable.";
        }
    }
}
