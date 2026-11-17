using CloneDeploy_Imager.Discovery;
using CloneDeploy_Imager.Imaging;
using Spectre.Console;

namespace CloneDeploy_Imager.UI;

/// <summary>
/// Pairs a menu value and label with an optional reason that prevents selection.
/// </summary>
internal sealed record MenuChoice(string Value, string Label, string? UnavailableReason = null);

/// <summary>
/// Owns console rendering state and provides common headings, messages, and inventory output.
/// </summary>
internal sealed partial class ImagerConsole
{
    internal const string ApplicationTitle = "CloneDeploy";
    public string? DeviceName { get; set; }
    private readonly IAnsiConsole console;
    private readonly bool plain;
    private readonly List<string> menuDetails = [];
    private (string Title, Color Color)? menuHeading;
    public ImagerConsole(bool plain)
    {
        this.plain = plain || Console.IsOutputRedirected;
        console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = this.plain ? AnsiSupport.No : AnsiSupport.Detect,
            ColorSystem = this.plain ? ColorSystemSupport.NoColors : ColorSystemSupport.Detect
        });
    }

    public void Heading()
    {
        menuDetails.Clear();
        if (!plain) console.Clear();
        else
        {
            console.WriteLine(ApplicationTitle);
            console.WriteLine();
        }
    }

    public void Line(string text)
    {
        var clean = Clean(text);
        console.WriteLine(clean);
        menuDetails.Add(clean);
        if (menuDetails.Count > 4) menuDetails.RemoveAt(0);
    }
    public void Error(string message, string logPath)
    {
        console.Write(new Text("Error: " + Clean(message), new Style(Color.Red)));
        console.WriteLine();
        menuDetails.Add("Error: " + Clean(message));
        Line("Log: " + logPath);
    }

    public void Inventory(HardwareInventory inventory)
    {
        Line($"{inventory.Manufacturer} {inventory.Model} | Serial: {inventory.Serial} | UUID: {inventory.Uuid}");
        Line(inventory.IsWinPe ? "Environment: WinPE" : "Environment: full Windows (discovery only)");
        foreach (var nic in inventory.Networks) Line($"{nic.Name}: {string.Join(", ", nic.Addresses)} | MAC: {nic.MacAddress}");
        console.WriteLine();
        foreach (var disk in inventory.Disks)
            Line($"Disk {disk.Number}: {disk.Name} | {disk.Size / 1073741824d:F1} GiB | Serial: {disk.Serial} | " +
                $"{(disk.PartitionStyle == 2 ? "GPT" : disk.PartitionStyle == 1 ? "MBR" : "RAW")} | " +
                $"{(disk.Offline ? "Offline" : "Online")}{(disk.ReadOnly ? " | Read-only" : "")}");
        console.WriteLine();
        foreach (var volume in inventory.WindowsVolumes)
            Line($"Windows: disk {volume.DiskNumber}, partition {volume.PartitionNumber}, {volume.Root}" +
                (volume.RunningWindows ? " (running OS; not an offline capture source)" : ""));
        if (inventory.WindowsVolumes.Count == 0) Line("No Windows installations were identified on accessible volumes.");
        foreach (var notice in inventory.Notices) Line(notice);
    }

    private static string Clean(string text) => new(text.Where(c => !char.IsControl(c)).ToArray());
}
