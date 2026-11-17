using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using Avalonia.Styling;

namespace CloneDeploy_Agent.MacOS.Portal;
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (!OperatingSystem.IsMacOS()) return;
        PortalText.Initialize();
        using (var instance = PortalInstance.Acquire())
        {
            if (instance is null) return;
            PortalApp.Instance = instance;
            PortalApp.OpenAfterRestart = instance.ConsumeRestartRequest();
            AppBuilder.Configure<PortalApp>().UsePlatformDetect().LogToTrace()
                .With(new MacOSPlatformOptions { ShowInDock = false })
                .StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        }
        // Release the single-instance lock only after the UI has shut down.
        if (PortalApp.RestartPath is { } path) PortalRestart.Execute(path);
    }
}
public sealed class PortalApp : Application
{
    internal static PortalInstance? Instance { get; set; }
    internal static string? RestartPath { get; private set; }
    internal static bool OpenAfterRestart { get; set; }
    internal void Restart()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            throw new IOException(PortalText.Get("Portal.RestartManually"));
        var path = Environment.ProcessPath;
        if (!OperatingSystem.IsMacOS() || path is null || !File.Exists(path) || Path.GetFileName(path) != "CloneDeploy-Agent.MacOS.Portal")
            throw new IOException(PortalText.Get("Portal.ExecutableUnavailable"));
        Instance!.RequestRestart();
        RestartPath = path;
        desktop.Shutdown();
    }
    private PortalWindow? window;
    private TrayIcon? tray;
    public override void Initialize()
    {
        Name = "CloneDeploy";
        RequestedThemeVariant = ThemeVariant.Default;
        Styles.Add(new FluentTheme());
        Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://CloneDeploy-Agent.MacOS.Portal/"))
            { Source = new Uri("avares://CloneDeploy-Agent.MacOS.Portal/Presentation/PortalStyles.axaml") });
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            window = new PortalWindow();
            // The desktop lifetime automatically shows MainWindow on startup.
            // Keep background services initialized, but assign it only when opened.
            void OpenPortal()
            {
                window ??= new PortalWindow();
                desktop.MainWindow = window;
                window.Show();
                if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                window.Activate();
            }
            Instance!.OpenRequested += () => Avalonia.Threading.Dispatcher.UIThread.Post(OpenPortal);
            if (this.TryGetFeature<IActivatableLifetime>() is { } activation)
                activation.Activated += (_, args) =>
                {
                    if (args.Kind == ActivationKind.Reopen)
                        Avalonia.Threading.Dispatcher.UIThread.Post(OpenPortal);
                };
            var open = new NativeMenuItem(PortalText.Get("Portal.Open"));
            open.Click += (_, _) => OpenPortal();
            var menu = new NativeMenu(); menu.Items.Add(open);
            tray = new TrayIcon { ToolTipText = PortalText.Get("Portal.Title"), Menu = menu,
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://CloneDeploy-Agent.MacOS.Portal/Assets/CloneDeploy.ico"))) };
            tray.Clicked += (_, _) => OpenPortal();
            TrayIcon.SetIcons(this, new TrayIcons { tray });
            desktop.Exit += (_, _) => { window?.Stop(); tray.Dispose(); };
            if (OpenAfterRestart || !(desktop.Args?.Contains("--background") ?? false)) OpenPortal();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
