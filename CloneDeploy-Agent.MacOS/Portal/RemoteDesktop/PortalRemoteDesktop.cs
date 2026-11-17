using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Agent.RemoteDesktop;

namespace CloneDeploy_Agent.MacOS.Portal;

internal sealed class PortalRemoteDesktop : IDisposable
{
    private readonly Window owner;
    private readonly Guid client = Guid.NewGuid();
    private readonly CancellationTokenSource stop = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap };
    private Window? prompt;
    private Guid? session;
    private AgentRemoteDesktopRequest? request;
    private CancellationTokenSource? sessionStop;
    private Task? streamTask;
    private bool polling, deciding, streaming, closingPrompt;
    internal Control Settings { get; }

    public PortalRemoteDesktop(Window owner)
    {
        this.owner = owner;
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = PortalText.Get("RemoteSupport.RemoteSupport"), FontSize = 18, FontWeight = FontWeight.SemiBold });
        error.IsVisible = false;
        error.PropertyChanged += (_, e) => { if (e.Property == TextBlock.TextProperty) error.IsVisible = !string.IsNullOrWhiteSpace(error.Text); };
        panel.Children.Add(error);
        var capture = new Button { Content = PortalText.Get("RemoteSupport.ScreenRecordingPermission") };
        var input = new Button { Content = PortalText.Get("RemoteSupport.AccessibilityPermission") };
        // Let macOS own the permission prompt; opening Settings as well duplicates its action.
        capture.Click += (_, _) =>
        {
            if (MacDesktopNative.CGPreflightScreenCaptureAccess()) OpenPreferences("Privacy_ScreenCapture");
            else MacDesktopNative.CGRequestScreenCaptureAccess();
        };
        input.Click += (_, _) =>
        {
            if (MacDesktopNative.CGPreflightPostEventAccess()) OpenPreferences("Privacy_Accessibility");
            else MacDesktopNative.CGRequestPostEventAccess();
        };
        panel.Children.Add(capture); panel.Children.Add(input);
        var restart = new Button { Content = PortalText.Get("RemoteSupport.RestartPortal") };
        restart.Click += (_, _) =>
        {
            try
            {
                if (Application.Current is not PortalApp app) throw new IOException(PortalText.Get("Portal.RestartUnavailable"));
                restart.IsEnabled = false;
                app.Restart();
            }
            catch (IOException ex) { error.Text = ex.Message; restart.IsEnabled = true; }
        };
        panel.Children.Add(restart);
        Settings = panel;
        timer.Tick += async (_, _) => await PollAsync();
        timer.Start();
    }

    private static void OpenPreferences(string section)
    {
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
            start.ArgumentList.Add("x-apple.systempreferences:com.apple.preference.security?" + section);
            using var process = System.Diagnostics.Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception) { }
    }
    private MacPortalAuthRequest Message(RemoteConsentAction action = RemoteConsentAction.Poll) =>
        new("remote-poll", RemoteClient: client, CanCapture: MacDesktopNative.ConsoleAvailable() && MacDesktopNative.CGPreflightScreenCaptureAccess(),
            CanControl: MacDesktopNative.CGPreflightPostEventAccess(), RemoteConsent: new(session, action));

    private async Task PollAsync()
    {
        if (polling || stop.IsCancellationRequested || !OperatingSystem.IsMacOS()) return;
        polling = true;
        try
        {
            var message = Message();
            var result = await PortalClient.AuthenticateAsync(message, stop.Token);
            if (stop.IsCancellationRequested) return;
            if (result.RemoteRequest is not { } incoming)
            {
                await EndStreamAsync(); ClosePrompt(); session = null; request = null; return;
            }
            if (session != incoming.SessionId)
            {
                await EndStreamAsync(); ClosePrompt(); session = incoming.SessionId; request = incoming; error.Text = "";
                if (incoming.AccessMode == RemoteAccessMode.AllowUnattended) await DecideAsync(true);
                else ShowPrompt(false);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or JsonException or OperationCanceledException)
        { await EndStreamAsync(); ClosePrompt(); session = null; request = null; }
        finally { polling = false; }
    }

    private void ShowPrompt(bool active)
    {
        ClosePrompt();
        var popup = prompt = new Window { Title = PortalText.Get("RemoteSupport.RemoteSupport"), Width = 380, SizeToContent = SizeToContent.Height,
            CanResize = false, ShowActivated = false, ShowInTaskbar = false, Topmost = true };
        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 12 };
        panel.Children.Add(PortalPopupBranding.Header(popup, active ? PortalText.Get("RemoteSupport.RemoteSupportConnected") : PortalText.Get("RemoteSupport.RemoteSupportRequest")));
        panel.Children.Add(new TextBlock { Text = active ? PortalText.Format("RemoteSupport.ConnectedToScreen", request!.Requester) :
            PortalText.Format("RemoteSupport.ConsentRequest", request!.Requester), TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold });
        if (!MacDesktopNative.CGPreflightPostEventAccess()) panel.Children.Add(new TextBlock { Text = PortalText.Get("RemoteSupport.ViewOnlyAccessibilityRequired"), TextWrapping = TextWrapping.Wrap });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (!active)
        {
            var allow = new Button { Content = PortalText.Get("Common.Allow") };
            allow.Click += async (_, _) => await DecideAsync(true); actions.Children.Add(allow);
        }
        var deny = new Button { Content = active ? PortalText.Get("Common.StopSharing") : PortalText.Get("Common.Deny") };
        deny.Click += async (_, _) => await DecideAsync(false); actions.Children.Add(deny); panel.Children.Add(actions);
        popup.Content = panel;
        popup.Closing += async (_, e) =>
        {
            if (!closingPrompt && e.CloseReason == WindowCloseReason.WindowClosing) { e.Cancel = true; await DecideAsync(false); }
        };
        PortalPopupPlacement.Configure(popup, owner);
        popup.Show();
    }
    private void ClosePrompt()
    {
        closingPrompt = true; prompt?.Close(); prompt = null; closingPrompt = false;
    }
    private async Task DecideAsync(bool allow)
    {
        if (deciding || session is null || request is null || stop.IsCancellationRequested) return;
        deciding = true;
        try
        {
            var id = session.Value;
            if (allow)
            {
                // Confirm capture actually works before reporting success to the technician.
                var displays = MacDesktopNative.Monitors();
                if (displays.Length == 0) throw new IOException(PortalText.Get("RemoteSupport.NoDisplayAvailable"));
                await Task.Run(() => MacDesktopNative.Capture(displays[0]), stop.Token);
                if (session != id || stop.IsCancellationRequested) return;
            }
            var result = await PortalClient.AuthenticateAsync(Message(allow ? RemoteConsentAction.Allow :
                streaming ? RemoteConsentAction.Stop : RemoteConsentAction.Deny), stop.Token);
            if (!allow) { sessionStop?.Cancel(); ClosePrompt(); return; }
            if (result.RemoteRequest?.SessionId != id) throw new IOException(PortalText.Get("RemoteSupport.RequestExpired"));
            if (streaming) return;
            sessionStop?.Dispose(); sessionStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            sessionStop.CancelAfter(TimeSpan.FromMinutes(30)); streaming = true;
            ShowPrompt(true);
            streamTask = RunAndFinishAsync(id, sessionStop);
        }
        catch (Exception ex)
        {
            error.Text = ex is IOException ? ex.Message : PortalText.Get("RemoteSupport.StartFailed");
            try { await PortalClient.AuthenticateAsync(Message(RemoteConsentAction.Deny), stop.Token); } catch { }
            ClosePrompt();
        }
        finally { deciding = false; }
    }

    private async Task EndStreamAsync()
    {
        sessionStop?.Cancel();
        if (streamTask is not null) await streamTask;
        streamTask = null; sessionStop?.Dispose(); sessionStop = null; streaming = false;
    }

    private async Task RunAndFinishAsync(Guid id, CancellationTokenSource lifetime)
    {
        try { await StreamAsync(id, lifetime.Token); }
        catch (Exception ex)
        { if (!lifetime.IsCancellationRequested) error.Text = PortalText.Get("RemoteSupport.EndedPrefix") + ex.Message; }
        finally
        {
            lifetime.Cancel();
            if (session == id) ClosePrompt();
            streaming = false;
            try { await PortalClient.AuthenticateAsync(Message() with { RemoteConsent = new(id, RemoteConsentAction.Stop) }, stop.Token); }
            catch (Exception ex) when (ex is IOException or SocketException or JsonException or OperationCanceledException) { }
        }
    }

    private async Task StreamAsync(Guid id, CancellationToken ct)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct); connect.CancelAfter(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(MacRemoteProtocol.SocketPath), connect.Token);
        MacDaemonGuard.VerifyPeer(socket);
        using var stream = new NetworkStream(socket);
        await MacPortalAuthProtocol.WriteAsync(stream, new MacRemoteAttach(id, client), connect.Token);
        if ((await MacPortalAuthProtocol.ReadAsync<MacPortalAuthReply>(stream, connect.Token)).Status != "RemoteAttached") throw new IOException(PortalText.Get("RemoteSupport.AttachmentRejected"));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var sends = new SemaphoreSlim(1);
        using var simulator = new MacDesktopInput();
        var stateGate = new object();
        string? selected = null;
        var changes = new DesktopPipe.FrameChanges();
        var frameDelay = DesktopPipe.FrameDelay("8");
        var quality = DesktopPipe.Quality("balanced");
        string? metadata = null;
        async Task Send(byte[] data)
        {
            await sends.WaitAsync(lifetime.Token);
            try { await DesktopPipe.WriteAsync(stream, data, lifetime.Token); }
            finally { sends.Release(); }
        }
        var frames = Task.Run(async () =>
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    byte[] frame, info;
                    lock (stateGate)
                    {
                        var displays = MacDesktopNative.Monitors();
                        var display = displays.FirstOrDefault(x => x.Id == selected) ?? displays.FirstOrDefault() ?? throw new IOException(PortalText.Get("RemoteSupport.NoDisplay"));
                        if (selected != display.Id) { simulator.Dispose(); selected = display.Id; }
                        simulator.Display = display;
                        info = DesktopPipe.ControlMessage(new { type = "monitors", selected, monitors = displays });
                        frame = MacDesktopNative.Capture(display, quality, changes);
                    }
                    var key = Convert.ToBase64String(info);
                    if (key != metadata) { await Send(info); metadata = key; }
                    if (frame.Length > 0) await Send(frame);
                    await Task.Delay(Volatile.Read(ref frameDelay), lifetime.Token);
                }
            }
            catch (Exception ex) when (!lifetime.IsCancellationRequested)
            {
                try { await Send(System.Text.Encoding.UTF8.GetBytes("ERR\n" + ex.Message)); } catch { }
                throw;
            }
        }, lifetime.Token);
        var input = Receive();
        try { await await Task.WhenAny(frames, input); }
        finally
        {
            lifetime.Cancel(); stream.Dispose(); socket.Dispose();
            try { await Task.WhenAll(frames, input); } catch (Exception) when (lifetime.IsCancellationRequested) { }
            lock (stateGate) simulator.Dispose();
        }
        async Task Receive()
        {
            while (!lifetime.IsCancellationRequested)
            {
                var data = await DesktopPipe.ReadAsync(stream, DesktopPipe.MaxInputBytes, lifetime.Token);
                using var document = JsonDocument.Parse(data);
                var root = document.RootElement;
                var kind = root.GetProperty("type").GetString();
                if (kind is not ("frameRate" or "quality" or "monitor" or "clipboardGet" or "clipboardSet" or "sas"))
                { lock (stateGate) simulator.Apply(root); continue; }
                var requestId = root.GetProperty("requestId").GetString();
                if (!Guid.TryParse(requestId, out _)) throw new IOException(PortalText.Get("RemoteSupport.InvalidCommand"));
                try
                {
                    if (!MacDesktopNative.ConsoleAvailable()) throw new IOException(PortalText.Get("RemoteSupport.ConsoleSessionUnavailable"));
                    if (kind == "frameRate")
                    {
                        Volatile.Write(ref frameDelay, DesktopPipe.FrameDelay(root.GetProperty("fps").GetString()));
                    }
                    else if (kind == "quality")
                    {
                        var nextQuality = DesktopPipe.Quality(root.GetProperty("preset").GetString());
                        lock (stateGate) quality = nextQuality;
                    }
                    else if (kind == "monitor")
                    {
                        var monitor = root.GetProperty("monitor").GetString();
                        lock (stateGate)
                        {
                            if (!MacDesktopNative.Monitors().Any(x => x.Id == monitor)) throw new IOException(PortalText.Get("RemoteSupport.MonitorUnavailable"));
                            simulator.Dispose(); selected = monitor;
                        }
                    }
                    else if (kind == "sas") throw new IOException(PortalText.Get("RemoteSupport.CtrlAltDeleteWindowsOnly"));
                    else
                    {
                        var clipboard = owner.Clipboard ?? throw new IOException(PortalText.Get("RemoteSupport.ClipboardUnavailable"));
                        if (kind == "clipboardGet")
                        {
                            var text = await Dispatcher.UIThread.InvokeAsync(() => { lifetime.Token.ThrowIfCancellationRequested(); if (!MacDesktopNative.ConsoleAvailable()) throw new IOException(PortalText.Get("RemoteSupport.ConsoleSessionUnavailable")); return clipboard.TryGetTextAsync(); });
                            if (text?.Length > DesktopPipe.MaxClipboardCharacters) throw new IOException(PortalText.Get("RemoteSupport.ClipboardTextExceedsTheLimit"));
                            await Send(DesktopPipe.ControlMessage(new { type = "clipboard", requestId, success = true, text = text ?? "" })); continue;
                        }
                        if (!MacDesktopNative.CGPreflightPostEventAccess()) throw new IOException(PortalText.Get("RemoteSupport.AccessibilityRequired"));
                        var value = root.GetProperty("text").GetString() ?? "";
                        if (value.Length > DesktopPipe.MaxClipboardCharacters || value.Contains('\0')) throw new IOException(PortalText.Get("RemoteSupport.ClipboardTextExceedsTheLimit"));
                        await Dispatcher.UIThread.InvokeAsync(() => { lifetime.Token.ThrowIfCancellationRequested(); if (!MacDesktopNative.ConsoleAvailable()) throw new IOException(PortalText.Get("RemoteSupport.ConsoleSessionUnavailable")); return clipboard.SetTextAsync(value); });
                    }
                    await Send(DesktopPipe.ControlMessage(new { type = "result", requestId, success = true, detail = kind == "frameRate" ? PortalText.Get("RemoteSupport.FrameRateUpdated") : kind == "quality" ? PortalText.Get("RemoteSupport.QualityUpdated") : kind == "monitor" ? PortalText.Get("RemoteSupport.MonitorSelected") : PortalText.Get("RemoteSupport.ClipboardTextSent") }));
                }
                catch (IOException ex) { await Send(DesktopPipe.ControlMessage(new { type = "result", requestId, success = false, detail = ex.Message })); }
            }
        }
    }
    public void Dispose()
    {
        if (stop.IsCancellationRequested) return;
        stop.Cancel(); timer.Stop(); sessionStop?.Cancel(); ClosePrompt();
    }
}
