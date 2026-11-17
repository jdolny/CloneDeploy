using System.IO.Pipes;
using System.Security.Principal;

namespace CloneDeploy_Agent.RemoteDesktop;

/// <summary>Hosts capture and input in the interactive session, without agent credentials or database access.</summary>
public static class DesktopHelper
{
    private const int AccessDenied = 5, InvalidHandle = 6, ResourceBusy = 170;
    private const int MaximumPendingReplies = 16;

    private const int PipeConnectTimeoutMilliseconds = 15000;

    private static readonly TimeSpan DesktopRecoveryTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(125);

    internal static bool IsDesktopUnavailable(Exception error) => error is DesktopUnavailableException ||
        error is System.ComponentModel.Win32Exception { NativeErrorCode: AccessDenied or InvalidHandle or ResourceBusy } ||
        error is System.Runtime.InteropServices.ExternalException and not System.ComponentModel.Win32Exception;

    internal static async Task<byte[]> CaptureWithRetryAsync(Func<byte[]> capture, TimeSpan recoveryTimeout, CancellationToken ct)
    {
        var recovery = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return capture(); }
            catch (Exception ex) when (IsDesktopUnavailable(ex) && recovery.Elapsed < recoveryTimeout)
            {
                await Task.Delay(FrameInterval, ct);
            }
        }
    }

    public static async Task RunAsync(string pipeName)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!identity.IsSystem)
            throw new UnauthorizedAccessException("Desktop helper requires the agent service identity.");
        const string prefix = "CloneDeploy-Desktop-";
        if (!pipeName.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(pipeName[prefix.Length..], "N", out _))
            throw new ArgumentException("Invalid desktop pipe name.");
        DesktopNative.SetProcessDPIAware();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Anonymous);
        await pipe.ConnectAsync(PipeConnectTimeoutMilliseconds, lifetime.Token);
        DesktopPipeTrust.ValidateServer(pipe);
        var replies = System.Threading.Channels.Channel.CreateBounded<byte[]>(MaximumPendingReplies);
        string? monitorId = null;
        var changes = new DesktopPipe.FrameChanges();
        var frameDelay = DesktopPipe.FrameDelay("8");
        var quality = DesktopPipe.Quality("balanced");
        string? lastMonitors = null;
        void Reply(object response)
        {
            if (!replies.Writer.TryWrite(DesktopPipe.ControlMessage(response))) throw new IOException("Too many pending desktop commands.");
        }
        var capture = Task.Run(async () =>
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    // Retry only capture: a broken pipe must still terminate the session immediately.
                    var frame = await CaptureWithRetryAsync(() =>
                    {
                        using var desktop = new DesktopAttachment();
                        var displays = DesktopMonitors.List();
                        var selected = displays.FirstOrDefault(x => x.Id == Volatile.Read(ref monitorId)) ?? displays.FirstOrDefault()
                            ?? throw new DesktopUnavailableException("No monitors are available.");
                        var metadata = DesktopPipe.ControlMessage(new { type = "monitors", selected = selected.Id, monitors = displays });
                        var key = Convert.ToBase64String(metadata);
                        if (key != lastMonitors) { if (!replies.Writer.TryWrite(metadata)) throw new IOException("Too many pending desktop commands."); lastMonitors = key; }
                        return DesktopCapture.CaptureMonitor(selected.Id, Volatile.Read(ref quality), changes);
                    }, DesktopRecoveryTimeout, lifetime.Token);
                    while (replies.Reader.TryRead(out var reply)) await DesktopPipe.WriteAsync(pipe, reply, lifetime.Token);
                    if (frame.Length > 0) await DesktopPipe.WriteAsync(pipe, frame, lifetime.Token);
                    await Task.Delay(Volatile.Read(ref frameDelay), lifetime.Token);
                }
            }
            catch (Exception ex) when (!lifetime.IsCancellationRequested)
            {
                // Report the capture failure before closing the pipe; the service owns the agent log.
                using var reportTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await DesktopPipe.WriteErrorAsync(pipe, ex, reportTimeout.Token);
                Environment.ExitCode = 1;
            }
        });
        var input = Task.Run(async () =>
        {
            using var simulator = new DesktopInput();
            while (!lifetime.IsCancellationRequested)
            {
                var message = await DesktopPipe.ReadAsync(pipe, DesktopPipe.MaxInputBytes, lifetime.Token);
                using var document = System.Text.Json.JsonDocument.Parse(message);
                var root = document.RootElement;
                var kind = root.GetProperty("type").GetString();
                if (kind is "frameRate" or "quality" or "monitor" or "clipboardGet" or "clipboardSet")
                {
                    var requestId = root.GetProperty("requestId").GetString();
                    if (!Guid.TryParse(requestId, out _)) throw new InvalidDataException("Invalid desktop command ID.");
                    try
                    {
                        if (kind == "frameRate")
                        {
                            Volatile.Write(ref frameDelay, DesktopPipe.FrameDelay(root.GetProperty("fps").GetString()));
                            Reply(new { type = "result", requestId, success = true, detail = "Frame rate updated." });
                        }
                        else if (kind == "quality")
                        {
                            Volatile.Write(ref quality, DesktopPipe.Quality(root.GetProperty("preset").GetString()));
                            Reply(new { type = "result", requestId, success = true, detail = "Viewing quality updated." });
                        }
                        else if (kind == "monitor")
                        {
                            var selected = root.GetProperty("monitor").GetString();
                            using var desktop = new DesktopAttachment();
                            if (!DesktopMonitors.List().Any(x => x.Id == selected)) throw new IOException("The selected monitor is unavailable.");
                            simulator.Dispose();
                            simulator.MonitorId = selected;
                            Volatile.Write(ref monitorId, selected);
                            Reply(new { type = "result", requestId, success = true, detail = "Monitor selected." });
                        }
                        else if (kind == "clipboardGet")
                            Reply(new { type = "clipboard", requestId, success = true, text = DesktopClipboard.Exchange(null) });
                        else
                        {
                            DesktopClipboard.Exchange(root.GetProperty("text").GetString() ?? "");
                            Reply(new { type = "result", requestId, success = true, detail = "Text sent to remote clipboard." });
                        }
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or System.ComponentModel.Win32Exception)
                    {
                        Reply(new { type = "result", requestId, success = false, detail = ex.Message });
                    }
                    continue;
                }
                try { simulator.Apply(message); }
                catch (Exception ex) when (IsDesktopUnavailable(ex))
                {
                    // Drop input during a desktop transition; never replay queued clicks into a UAC prompt.
                    // The capture loop reports persistent desktop access failures.
                    simulator.Dispose();
                }
            }
        });
        await Task.WhenAny(capture, input);
        await lifetime.CancelAsync();
        pipe.Dispose();
        try { await Task.WhenAll(capture, input); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }
}
