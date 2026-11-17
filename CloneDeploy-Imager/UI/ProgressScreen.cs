using CloneDeploy_Imager.Discovery;
using CloneDeploy_Imager.Imaging;
using Spectre.Console;

namespace CloneDeploy_Imager.UI;

/// <summary>
/// Displays imaging stage progress and elapsed time in interactive or plain console output.
/// </summary>
internal sealed partial class ImagerConsole
{
    public async Task ShowProgressAsync(IAsyncEnumerable<DeploymentProgress> updates, CancellationToken ct, bool capture = false)
    {
        await using var reader = updates.GetAsyncEnumerator(ct);
        DeploymentProgress? pending = null;
        while (await reader.MoveNextAsync())
        {
            pending = reader.Current;
            await RenderAsync(Segment());
            if (pending?.Interaction is { } interaction) await interaction(ct);
        }

        async IAsyncEnumerable<DeploymentProgress> Segment()
        {
            do
            {
                pending = reader.Current;
                if (pending.Interaction is not null) yield break;
                yield return pending;
            } while (await reader.MoveNextAsync());
            pending = null;
        }

        async Task RenderAsync(IAsyncEnumerable<DeploymentProgress> segment)
        {
            if (plain || !console.Profile.Capabilities.Ansi)
            {
                // Redirected/limited consoles get stage boundaries only, never hundreds of progress lines.
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                string? lastStatus = null;
                DeploymentStage? last = null;
                await foreach (var update in segment.WithCancellation(ct))
                {
                    if (last != update.Stage) { Line(Label(update.Stage, capture)); last = update.Stage; }
                    if (update.Status is not null && update.Status != lastStatus) Line(update.Status);
                    lastStatus = update.Status;
                    if (update.Complete) Line((Label(update.Stage, capture)) + " 100%");
                }
                Line($"Total elapsed  {(int)elapsed.Elapsed.TotalHours:00}:{elapsed.Elapsed.Minutes:00}:{elapsed.Elapsed.Seconds:00}");
                return;
            }
            console.Clear();
            var phase = new CurrentPhaseColumn(capture, console);
            await console.Progress().AutoClear(false).AutoRefresh(true).HideCompleted(false)
                .Columns(phase)
                .UseRenderHook((renderable, _) => new Align(renderable, HorizontalAlignment.Center, VerticalAlignment.Middle)
                {
                    Width = console.Profile.Width,
                    // Leave a row for the cursor so a full-height render does not scroll the console.
                    Height = Math.Max(1, console.Profile.Height - 1)
                })
                .StartAsync(async context =>
                {
                    // Reuse one display so previous and future phases never appear alongside the current phase.
                    var task = context.AddTask("Preparing", autoStart: true);
                    try
                    {
                        await foreach (var update in segment.WithCancellation(ct))
                        {
                            phase.Stage = update.Stage;
                            phase.PhaseComplete = update.Complete;
                            phase.Receiving = update.Receiving;
                            phase.Status = update.Status;
                            task.Description = Label(update.Stage, capture);
                            task.IsIndeterminate = update.Percent is null && !update.Complete;
                            task.Value = update.Complete ? 100 : Math.Clamp(update.Percent ?? 0, 0, 100);
                            context.Refresh();
                        }
                    }
                    finally { phase.Stop(); task.StopTask(); context.Refresh(); }
                });
        }
    }

    private static string Label(DeploymentStage stage, bool capture) => stage switch
    {
        DeploymentStage.Preparing => capture ? "Preparing to upload image" : "Preparing to deploy image",
        DeploymentStage.Deploying => capture ? "Uploading image" : "Deploying image",
        DeploymentStage.Finalizing => "Finalizing image",
        _ => throw new ArgumentOutOfRangeException(nameof(stage))
    };
}
