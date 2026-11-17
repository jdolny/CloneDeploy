using Spectre.Console;
using Spectre.Console.Rendering;

namespace CloneDeploy_Imager.UI;

/// <summary>
/// Displays centered messages and handles returning from a screen with Escape.
/// </summary>
internal sealed partial class ImagerConsole
{
    public async Task WaitForBackAsync(CancellationToken ct)
    {
        // Cancelled forms and menus have nothing to acknowledge; return directly.
        if (menuDetails.Count == 0) return;
        var details = menuDetails.ToArray();
        if (plain || !console.Profile.Capabilities.Ansi || !console.Profile.Capabilities.Interactive)
        {
            Line("Esc to go back");
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Escape) break;
                await Task.Delay(30, ct);
            }
            menuDetails.Clear();
            return;
        }

        console.Clear();
        IRenderable Screen()
        {
            var content = new List<IRenderable>
            {
                new Text(ApplicationTitle, new Style(Color.Grey)).Centered(),
                new Text("")
            };
            if (DeviceName is not null)
                content.Add(new Text("Device Name: " + Clean(DeviceName), new Style(Color.Cyan1)).Centered());
            foreach (var detail in details)
                content.Add(new Text(detail, new Style(detail.StartsWith("Error:", StringComparison.Ordinal) ? Color.Red : Color.White)));
            var card = new Panel(new Rows(content)) { Width = Math.Max(20, Math.Min(console.Profile.Width - 4, 76)) }
                .Border(BoxBorder.Rounded).BorderColor(Color.Cyan1).Padding(2, 1);
            return new Rows(new Align(card, HorizontalAlignment.Center, VerticalAlignment.Middle)
                { Width = console.Profile.Width, Height = Math.Max(1, console.Profile.Height - 3) },
                new Text("Esc to go back", new Style(Color.Grey)).Centered());
        }
        await console.Live(Screen()).AutoClear(true).StartAsync(async context =>
        {
            context.Refresh();
            var dimensions = (console.Profile.Width, console.Profile.Height);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Escape) break;
                var current = (console.Profile.Width, console.Profile.Height);
                if (current != dimensions) { dimensions = current; context.UpdateTarget(Screen()); context.Refresh(); }
                await Task.Delay(30, ct);
            }
        });
        menuDetails.Clear();
    }
}
