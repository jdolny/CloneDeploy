using CloneDeploy_Imager.Imaging;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace CloneDeploy_Imager.UI;

/// <summary>A single, expanded progress screen shared by capture and deployment.</summary>
internal sealed class CurrentPhaseColumn(bool capture, IAnsiConsole console) : ProgressColumn
{
    private const int SpinnerFrameMilliseconds = 150;
    private const double BytesPerMiB = 1048576;
    private const double BytesPerGiB = 1073741824;

    private readonly NetworkTransferRate rate = capture ? new() : NetworkTransferRate.ForReceive();
    private readonly NetworkTransferRate receiveRate = NetworkTransferRate.ForReceive();
    public bool Receiving { get; set; }
    private readonly System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
    public string? Status { get; set; }
    public DeploymentStage Stage { get; set; }
    public bool PhaseComplete { get; set; }
    public void Stop() => elapsed.Stop();

    public override IRenderable Render(RenderOptions options, ProgressTask task, TimeSpan deltaTime)
    {
        var duration = elapsed.Elapsed;
        var width = Math.Clamp(console.Profile.Width - 12, 10, 100);
        var percent = (int)Math.Clamp(task.Value, 0, 100);
        var filled = task.IsIndeterminate ? 0 : percent * width / 100;
        var bar = new string('█', filled) + new string('░', width - filled);
        // ASCII frames remain readable with the limited fonts available in WinPE.
        var frames = Spinner.Known.Line.Frames;
        var spinner = frames[(int)(duration.TotalMilliseconds / SpinnerFrameMilliseconds % frames.Count)];
        var showBar = !task.IsIndeterminate && !PhaseComplete;
        var status = showBar ? (Status is { Length: > 0 } message ? $"{message} — {percent}%" : $"{percent}% complete")
            : Stage == DeploymentStage.Finalizing && PhaseComplete ? "Complete" : Status ?? spinner + "  Working";
        var rows = new List<IRenderable>
        {
            new Text(ImagerConsole.ApplicationTitle, new Style(Color.Grey)).Centered(),
            new Text(""),
            new Text(task.Description.ToUpperInvariant(), new Style(Color.Cyan1, decoration: Decoration.Bold)).Centered(),
            new Text(""),
            new Text(showBar ? bar + "\n" + bar : "\n", new Style(Color.Cyan1)).Centered(),
            new Text(""),
            new Text(status).Centered(),
            new Text($"Total elapsed  {(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}").Centered()
        };
        var bytes = rate.Sample();
        var received = receiveRate.Sample();
        if (capture && Receiving) bytes = received;
        var direction = capture && !Receiving ? "TX" : "RX";
        rows.Add(new Text(""));
        rows.Add(new Text(bytes is { } value
            ? $"Network {direction}  {value / BytesPerMiB:0.0} MiB/s  |  {value * 60 / BytesPerGiB:0.00} GiB/min"
            : $"Network {direction}  --").Centered());
        return new Panel(new Rows(rows)) { Width = Math.Max(16, Math.Min(console.Profile.Width - 4, 112)) }
            .Border(BoxBorder.Rounded).Padding(2, 1);
    }
}
