using CloneDeploy_Imager.Discovery;
using CloneDeploy_Imager.Imaging;
using Spectre.Console;

namespace CloneDeploy_Imager.UI;

/// <summary>
/// Renders image confirmations and selectable menus, including unavailable choices and their reasons.
/// </summary>
internal sealed partial class ImagerConsole
{
    private int menuWidth = 76;

    public async Task<string> ChooseOuAsync(string title, string message, MenuChoice[] choices, CancellationToken ct)
    {
        Heading();
        menuHeading = (Clean(message), Color.White);
        menuWidth = int.MaxValue;
        try { return await ChooseAvailableAsync(title, choices, ct); }
        finally { menuHeading = null; menuWidth = 76; }
    }

    public async Task<string> ShowSuccessAsync(string message, CancellationToken ct)
    {
        Heading();
        menuHeading = (Clean(message), Color.Green);
        try { return await ChooseAsync("", ["Reboot", "Shutdown", "Main menu"], ct); }
        finally { menuHeading = null; }
    }

    public async Task<bool> ConfirmUploadAsync(string imageName, CancellationToken ct)
    {
        menuHeading = ("Destination Image: " + Clean(imageName), Color.Green);
        try
        {
            Line($"Image will be uploaded to a new version of {imageName}.");
            return await ChooseAsync("", ["Cancel", "Upload now"], ct) == "Upload now";
        }
        finally { menuHeading = null; }
    }

    public async Task<string> ChooseAsync(string title, string[] choices, CancellationToken ct)
    {
        if (Console.IsInputRedirected) throw new InvalidOperationException("Interactive input requires a keyboard console.");
        if (!plain && console.Profile.Capabilities.Ansi && console.Profile.Capabilities.Interactive)
            return await ChooseCenteredAsync(title, choices.Select(x => new MenuChoice(x, x)).ToArray(), ct);
        if (DeviceName is not null) Line("Device Name: " + DeviceName);
        if (menuHeading is { } heading) Line(heading.Title);
        if (!string.IsNullOrWhiteSpace(title)) Line(title);
        for (var i = 0; i < choices.Length; i++) Line($"{i + 1}. {choices[i]}");
        while (true)
        {
            var answer = await AskAsync("Number:", false, ct);
            if (answer.Length == 0)
            {
                var back = choices.FirstOrDefault(x => x is "Cancel" or "Main menu");
                if (back is not null) { menuDetails.Clear(); return back; }
                continue;
            }
            if (int.TryParse(answer, out var index) && index >= 1 && index <= choices.Length)
            {
                menuDetails.Clear();
                return choices[index - 1];
            }
        }
    }

    public async Task<string> ChooseAvailableAsync(string title, MenuChoice[] choices, CancellationToken ct)
    {
        // Keep unavailable choices visible, but never return one to the caller.
        if (!choices.Any(x => x.UnavailableReason is null)) throw new ArgumentException("At least one choice must be available.");
        string Label(MenuChoice choice) => Clean(choice.Label + (choice.UnavailableReason is null ? "" : " (unavailable: " + choice.UnavailableReason + ")"));
        while (true)
        {
            MenuChoice selected;
            if (!plain && console.Profile.Capabilities.Ansi && console.Profile.Capabilities.Interactive)
                return await ChooseCenteredAsync(title, choices, ct);
            else
            {
                var labels = choices.Select(Label).ToArray();
                var answer = await ChooseAsync(title, labels, ct);
                selected = choices[Array.IndexOf(labels, answer)];
            }
            if (selected.UnavailableReason is null) return selected.Value;
            Line("Unavailable: " + selected.UnavailableReason);
        }
    }

    private async Task<string> ChooseCenteredAsync(string title, MenuChoice[] choices, CancellationToken ct)
    {
        if (choices.Length == 0) throw new ArgumentException("At least one choice is required.");
        console.Clear();
        var details = menuDetails.ToArray();
        var backIndex = Array.FindIndex(choices, x => x.Value is "Cancel" or "Main menu");
        var selected = Array.FindIndex(choices, x => x.UnavailableReason is null);
        if (selected < 0) throw new ArgumentException("At least one choice must be available.");
        Spectre.Console.Rendering.IRenderable Screen()
        {
            var pageSize = Math.Max(1, Math.Min(10, (console.Profile.Height - 16 - details.Length * 2) / 2));
            var first = Math.Clamp(selected - pageSize / 2, 0, Math.Max(0, choices.Length - pageSize));
            var content = new List<Spectre.Console.Rendering.IRenderable>
            {
                new Text(ApplicationTitle, new Style(Color.Grey)).Centered(),
                new Text(""),
                new Text(Clean(DeviceName is null ? "Imaging" : "Device Name: " + DeviceName), new Style(Color.Cyan1, decoration: Decoration.Bold)).Centered()
            };
            content.Add(new Text(""));
            if (menuHeading is { } heading)
                content.Add(new Text(heading.Title, new Style(heading.Color, decoration: Decoration.Bold)).Centered());
            foreach (var detail in details) content.Add(new Text(detail, new Style(Color.Yellow)).Centered());
            if (!string.IsNullOrWhiteSpace(title)) content.Add(new Text(Clean(title)).Centered());
            content.Add(new Text(""));
            for (var i = first; i < Math.Min(choices.Length, first + pageSize); i++)
            {
                var choice = choices[i];
                var label = (i == selected ? "> " : "  ") + Clean(choice.Label);
                if (choice.UnavailableReason is not null) label += " (unavailable: " + Clean(choice.UnavailableReason) + ")";
                content.Add(new Text(label, new Style(choice.UnavailableReason is not null ? Color.Grey :
                    i == selected ? Color.Cyan1 : Color.White, decoration: i == selected ? Decoration.Bold : Decoration.None)));
                content.Add(new Text(""));
            }
            if (choices.Length > pageSize)
                content.Add(new Text($"{first + 1}-{Math.Min(choices.Length, first + pageSize)} of {choices.Length}", new Style(Color.Grey)).Centered());
            var card = new Panel(new Rows(content)) { Width = Math.Max(20, Math.Min(console.Profile.Width - 4, menuWidth)) }
                .Border(BoxBorder.Rounded).BorderColor(Color.Cyan1).Padding(2, 1);
            return new Rows(new Align(card, HorizontalAlignment.Center, VerticalAlignment.Middle)
                { Width = console.Profile.Width, Height = Math.Max(1, console.Profile.Height - 4) },
                new Text("Up/Down to select   Enter to continue" + (backIndex >= 0 ? "   Esc to go back" : ""), new Style(Color.Grey)).Centered());
        }
        // Include preceding task details and destructive-action warnings inside the menu.
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
                if (key.Key == ConsoleKey.Escape && backIndex >= 0) { selected = backIndex; break; }
                if (key.Key == ConsoleKey.Enter && choices[selected].UnavailableReason is null) break;
                selected = key.Key switch
                {
                    ConsoleKey.UpArrow => (selected + choices.Length - 1) % choices.Length,
                    ConsoleKey.DownArrow => (selected + 1) % choices.Length,
                    ConsoleKey.Home => 0,
                    ConsoleKey.End => choices.Length - 1,
                    _ => selected
                };
                context.UpdateTarget(Screen());
                context.Refresh();
            }
        });
        menuDetails.Clear();
        return choices[selected].Value;
    }
}
