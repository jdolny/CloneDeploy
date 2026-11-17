using CloneDeploy_Imager.Discovery;
using CloneDeploy_Imager.Imaging;
using Spectre.Console;

namespace CloneDeploy_Imager.UI;

/// <summary>
/// Collects centered text or masked input with keyboard navigation and Escape cancellation.
/// </summary>
internal sealed partial class ImagerConsole
{
    private const int MaximumInputCharacters = 1024;

    public async Task<string> AskLoginAsync(string label, bool secret, string? userName, CancellationToken ct)
    {
        if (Console.IsInputRedirected) throw new InvalidOperationException("Interactive input requires a keyboard console.");
        if (plain || !console.Profile.Capabilities.Ansi || !console.Profile.Capabilities.Interactive)
        {
            Heading();
            Line("Sign in");
            if (userName is not null) Line(userName);
            Line("Enter to continue | Esc to go back");
            return await AskAsync(label + ":", secret, ct);
        }

        console.Clear();
        var value = new System.Text.StringBuilder();
        Spectre.Console.Rendering.IRenderable Screen()
        {
            var fieldWidth = Math.Clamp(console.Profile.Width - 12, 8, 48);
            var visible = secret ? new string('*', value.Length) : Clean(value.ToString());
            if (visible.Length > fieldWidth - 1) visible = visible[^(fieldWidth - 1)..];
            var content = new List<Spectre.Console.Rendering.IRenderable>
            {
                new Text(ApplicationTitle, new Style(Color.Grey)).Centered(),
                new Text(""),
                new Text("SIGN IN", new Style(Color.Cyan1, decoration: Decoration.Bold)).Centered(),
                new Text("")
            };
            if (userName is not null)
            {
                content.Add(new Text(Clean(userName), new Style(Color.Grey)).Centered());
                content.Add(new Text(""));
            }
            content.Add(new Text(label));
            content.Add(new Panel(new Text(visible + "_")) { Width = fieldWidth + 2 }
                .Border(BoxBorder.Rounded).BorderColor(Color.Cyan1));
            var card = new Panel(new Rows(content)) { Width = Math.Max(20, Math.Min(console.Profile.Width - 4, 56)) }
                .Border(BoxBorder.Rounded).Padding(2, 1);
            return new Rows(
                new Align(card, HorizontalAlignment.Center, VerticalAlignment.Middle)
                    { Width = console.Profile.Width, Height = Math.Max(1, console.Profile.Height - 4) },
                new Text("Enter to continue | Esc to go back", new Style(Color.Grey)).Centered());
        }

        await console.Live(Screen()).AutoClear(true).StartAsync(async context =>
        {
            context.Refresh();
            var dimensions = (console.Profile.Width, console.Profile.Height);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (!Console.KeyAvailable)
                {
                    await Task.Delay(30, ct);
                    var current = (console.Profile.Width, console.Profile.Height);
                    if (current != dimensions) { dimensions = current; context.UpdateTarget(Screen()); context.Refresh(); }
                    continue;
                }
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Escape) { value.Clear(); break; }
                if (key.Key == ConsoleKey.Enter && !string.IsNullOrWhiteSpace(value.ToString())) break;
                if (key.Key == ConsoleKey.Backspace && value.Length > 0) value.Length--;
                else if (!char.IsControl(key.KeyChar) && value.Length < MaximumInputCharacters) value.Append(key.KeyChar);
                context.UpdateTarget(Screen());
                context.Refresh();
            }
        });
        return value.ToString();
    }

    public async Task<string> AskAsync(string label, bool secret, CancellationToken ct)
    {
        if (Console.IsInputRedirected) throw new InvalidOperationException("Interactive input requires a keyboard console.");
        if (!plain && console.Profile.Capabilities.Ansi && console.Profile.Capabilities.Interactive)
            return await AskCenteredAsync(label, secret, ct);
        console.Write(Clean(label) + " (Esc to go back) ");
        var value = new System.Text.StringBuilder();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (!Console.KeyAvailable) { await Task.Delay(30, ct); continue; }
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Escape) { console.WriteLine(); return ""; }
            if (key.Key == ConsoleKey.Enter && !string.IsNullOrWhiteSpace(value.ToString())) { console.WriteLine(); return value.ToString(); }
            if (key.Key == ConsoleKey.Backspace && value.Length > 0)
            {
                value.Length--;
                Console.Write("\b \b");
            }
            else if (!char.IsControl(key.KeyChar))
            {
                value.Append(key.KeyChar);
                Console.Write(secret ? '*' : key.KeyChar);
            }
        }
    }

    private async Task<string> AskCenteredAsync(string label, bool secret, CancellationToken ct)
    {
        var value = new System.Text.StringBuilder();
        // Keep hardware results, errors, and task output visible above the input card.
        var top = Console.CursorTop;
        Spectre.Console.Rendering.IRenderable Screen()
        {
            var cardWidth = Math.Max(20, Math.Min(console.Profile.Width - 4, 56));
            var fieldWidth = Math.Max(8, cardWidth - 8);
            var visible = secret ? new string('*', value.Length) : Clean(value.ToString());
            if (visible.Length > fieldWidth - 1) visible = visible[^(fieldWidth - 1)..];
            var content = new List<Spectre.Console.Rendering.IRenderable>
            {
                new Text(ApplicationTitle, new Style(Color.Grey)).Centered(),
                new Text("")
            };
            if (DeviceName is not null)
            {
                content.Add(new Text("Device Name: " + Clean(DeviceName), new Style(Color.Cyan1, decoration: Decoration.Bold)).Centered());
                content.Add(new Text(""));
            }
            content.Add(new Text(Clean(label)));
            content.Add(new Panel(new Text(visible + "_")) { Width = fieldWidth + 2 }
                .Border(BoxBorder.Rounded).BorderColor(Color.Cyan1));
            var card = new Panel(new Rows(content)) { Width = cardWidth }
                .Border(BoxBorder.Rounded).BorderColor(Color.Cyan1).Padding(2, 1);
            return new Rows(new Align(card, HorizontalAlignment.Center, VerticalAlignment.Middle)
                { Width = console.Profile.Width, Height = Math.Max(1, console.Profile.Height - top - 4) },
                new Text("Enter to continue | Esc to go back", new Style(Color.Grey)).Centered());
        }
        await console.Live(Screen()).AutoClear(true).StartAsync(async context =>
        {
            context.Refresh();
            var dimensions = (console.Profile.Width, console.Profile.Height);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (!Console.KeyAvailable)
                {
                    await Task.Delay(30, ct);
                    var current = (console.Profile.Width, console.Profile.Height);
                    if (current != dimensions) { dimensions = current; context.UpdateTarget(Screen()); context.Refresh(); }
                    continue;
                }
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Escape) { value.Clear(); break; }
                if (key.Key == ConsoleKey.Enter && !string.IsNullOrWhiteSpace(value.ToString())) break;
                if (key.Key == ConsoleKey.Backspace && value.Length > 0) value.Length--;
                else if (!char.IsControl(key.KeyChar) && value.Length < MaximumInputCharacters) value.Append(key.KeyChar);
                context.UpdateTarget(Screen());
                context.Refresh();
            }
        });
        return value.ToString();
    }
}
