using CloneDeploy_Imager.Discovery;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace CloneDeploy_Imager.UI;

/// <summary>
/// Displays discovered hardware and Windows installations in a keyboard-navigable screen.
/// </summary>
internal sealed partial class ImagerConsole
{
    public async Task ShowHardwareAsync(HardwareInventory inventory, CancellationToken ct)
    {
        Heading();
        if (plain || !console.Profile.Capabilities.Ansi || !console.Profile.Capabilities.Interactive)
        {
            if (DeviceName is not null) Line("Device Name: " + DeviceName);
            Inventory(inventory);
            if (Console.IsInputRedirected) return;
            Line("Esc to return to the menu");
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Escape) return;
                await Task.Delay(30, ct);
            }
        }

        var details = new List<(string Text, Color Color)>();
        void Section(string title)
        {
            if (details.Count > 0) details.Add(("", Color.White));
            details.Add((title, Color.Cyan1));
        }
        void Field(string label, string value) => details.Add(($"{label,-15} {Clean(string.IsNullOrWhiteSpace(value) ? "Not reported" : value)}", Color.White));

        Section("SYSTEM");
        Field("Manufacturer", inventory.Manufacturer);
        Field("Model", inventory.Model);
        Field("Serial", inventory.Serial);
        Field("UUID", inventory.Uuid);
        Field("Environment", inventory.IsWinPe ? "WinPE" : "Full Windows (discovery only)");

        Section("NETWORK ADAPTERS");
        if (inventory.Networks.Count == 0) details.Add(("No adapters with assigned IP addresses were found.", Color.Grey));
        foreach (var adapter in inventory.Networks)
        {
            Field("Adapter", adapter.Name);
            Field("MAC address", adapter.MacAddress);
            Field("IP addresses", string.Join(", ", adapter.Addresses));
            details.Add(("", Color.White));
        }

        Section("DISKS");
        if (inventory.Disks.Count == 0) details.Add(("No disks were detected.", Color.Grey));
        foreach (var disk in inventory.Disks)
        {
            Field("Disk " + disk.Number, disk.Name);
            Field("Capacity", $"{disk.Size / 1073741824d:F1} GiB");
            Field("Serial", disk.Serial);
            Field("Partition style", disk.PartitionStyle switch { 2 => "GPT", 1 => "MBR", _ => "RAW" });
            Field("Status", (disk.Offline ? "Offline" : "Online") + (disk.ReadOnly ? " | Read-only" : " | Writable"));
            details.Add(("", Color.White));
        }

        Section("WINDOWS INSTALLATIONS");
        if (inventory.WindowsVolumes.Count == 0)
            details.Add(("No Windows installations were identified on accessible volumes.", Color.Grey));
        foreach (var volume in inventory.WindowsVolumes)
        {
            Field("Location", $"Disk {volume.DiskNumber}, partition {volume.PartitionNumber}: {volume.Root}");
            Field("Status", volume.RunningWindows ? "Running OS; not an offline capture source" : "Offline installation");
            details.Add(("", Color.White));
        }
        if (inventory.Notices.Count > 0)
        {
            Section("DISCOVERY NOTICES");
            foreach (var notice in inventory.Notices) details.Add((Clean(notice), Color.Yellow));
        }

        var offset = 0;
        var pageSize = 1;
        var lineCount = 0;
        IRenderable Screen()
        {
            var width = Math.Max(20, Math.Min(console.Profile.Width - 4, 92));
            var contentWidth = width - 6;
            var lines = new List<IRenderable>();
            foreach (var (text, color) in details)
            {
                if (text.Length == 0) lines.Add(new Text(""));
                else
                {
                    // Wrap before paging so long identifiers, addresses, and notices remain accessible.
                    for (var start = 0; start < text.Length; start += contentWidth)
                        lines.Add(new Text(text.Substring(start, Math.Min(contentWidth, text.Length - start)), new Style(color)));
                }
            }
            lineCount = lines.Count;
            pageSize = Math.Max(1, console.Profile.Height - 13);
            offset = Math.Clamp(offset, 0, Math.Max(0, lineCount - pageSize));
            var content = new List<IRenderable>
            {
                new Text(ApplicationTitle, new Style(Color.Grey)).Centered(),
                new Text("HARDWARE INFORMATION", new Style(Color.Cyan1, decoration: Decoration.Bold)).Centered(),
                new Text(DeviceName is null ? "" : "Device Name: " + Clean(DeviceName)).Centered(),
                new Text("")
            };
            content.AddRange(lines.Skip(offset).Take(pageSize));
            content.Add(new Text(""));
            content.Add(new Text($"{offset + 1}-{Math.Min(offset + pageSize, lineCount)} of {lineCount}", new Style(Color.Grey)).Centered());
            var card = new Panel(new Rows(content)) { Width = width }
                .Border(BoxBorder.Rounded).BorderColor(Color.Cyan1).Padding(2, 1);
            return new Rows(new Align(card, HorizontalAlignment.Center, VerticalAlignment.Middle)
                { Width = console.Profile.Width, Height = Math.Max(1, console.Profile.Height - 2) },
                new Text("Up/Down: scroll | PgUp/PgDn: page | Home/End | Esc: back", new Style(Color.Grey)).Centered());
        }

        await console.Live(Screen()).AutoClear(true).StartAsync(async context =>
        {
            context.Refresh();
            var dimensions = (console.Profile.Width, console.Profile.Height);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var redraw = false;
                var current = (console.Profile.Width, console.Profile.Height);
                if (current != dimensions) { dimensions = current; redraw = true; }
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);
                    if (key.Key == ConsoleKey.Escape) break;
                    offset = key.Key switch
                    {
                        ConsoleKey.UpArrow => offset - 1,
                        ConsoleKey.DownArrow => offset + 1,
                        ConsoleKey.PageUp => offset - pageSize,
                        ConsoleKey.PageDown => offset + pageSize,
                        ConsoleKey.Home => 0,
                        ConsoleKey.End => lineCount,
                        _ => offset
                    };
                    redraw = true;
                }
                if (redraw) { context.UpdateTarget(Screen()); context.Refresh(); }
                await Task.Delay(30, ct);
            }
        });
    }
}
