using CloneDeploy_Agent;
using Microsoft.Extensions.Hosting.WindowsServices;

// Isolated printer RPCs inherit the calling policy's identity. This mode does
// not start the service, load enrollment state, or request elevated credentials.
if (args is ["--printer-action", var printerRequest])
{
    Environment.ExitCode = await PrinterActionRunner.RunHelperAsync(printerRequest);
    return;
}

// MSI executes this after stopping the service, under SYSTEM. Only the file path
// crosses the installer command line; credentials never become MSI properties.
if (args is ["--import-enrollment", var enrollmentPath])
{
    try
    {
        using var account = System.Security.Principal.WindowsIdentity.GetCurrent();
        if (!account.IsSystem) throw new UnauthorizedAccessException("Enrollment import requires SYSTEM.");
        var installed = new AgentOptions
        {
            StateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CloneDeploy", "Agent"),
            DeviceName = Environment.MachineName
        };
        using var installedStore = new AgentStore(installed.StateDirectory);
        InstallerEnrollment.ImportFile(installedStore, installed, enrollmentPath);
    }
    catch (Exception)
    {
        Console.Error.WriteLine("Enrollment import failed. Check the local enrollment file, its permissions, and the installation log.");
        Environment.ExitCode = ProcessExitCodes.Failure;
    }
    return;
}

if (args is ["--remove-agent-data"])
{
    try { AgentUninstallCleanup.Run(); }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Agent data cleanup failed: {ex.Message}");
        Environment.ExitCode = ProcessExitCodes.Failure;
    }
    return;
}

if (args is ["--finalize-image-uninstall", var uninstallDirectory, var uninstallMode] && uninstallMode is "capture" or "shutdown")
{
    Environment.ExitCode = await ImagePrepFinalizer.RunUninstallAsync(uninstallDirectory, uninstallMode == "capture");
    return;
}

if (args is ["--finalize-image-capture", var captureDirectory, var captureReset] && captureReset is "reset" or "keep")
{
    Environment.ExitCode = await ImagePrepFinalizer.RunCaptureAsync(captureDirectory, captureReset == "reset");
    return;
}

if (args is ["--finalize-image-prep", var prepDirectory, var prepReset] && prepReset is "reset" or "keep")
{
    Environment.ExitCode = await ImagePrepFinalizer.RunAsync(prepDirectory, prepReset == "reset");
    return;
}

if (args is ["--remote-desktop-helper", var pipeName])
{
    try
    {
        await CloneDeploy_Agent.RemoteDesktop.DesktopHelper.RunAsync(pipeName);
    }
    catch
    {
        Environment.ExitCode = ProcessExitCodes.Failure;
    }

    return;
}

var commands = new[] { "--reset", "--resetfull" };
var supported = commands;
if (args.Any(argument => !supported.Contains(argument, StringComparer.OrdinalIgnoreCase)))
    throw new ArgumentException("Unsupported agent argument. Supported: --reset, --resetfull. Install a configured MSI to change agent settings.");
var command = args.FirstOrDefault(argument => commands.Contains(argument, StringComparer.OrdinalIgnoreCase));
if (args.Count(argument => commands.Contains(argument, StringComparer.OrdinalIgnoreCase)) > 1)
    throw new ArgumentException("Run one maintenance command at a time.");
if (command is null && !WindowsServiceHelpers.IsWindowsService())
    throw new InvalidOperationException("Start the installed agent through Windows Services.");
using (var account = System.Security.Principal.WindowsIdentity.GetCurrent())
    if (!account.IsSystem) throw new InvalidOperationException("Run the installed agent and its maintenance commands as SYSTEM. Install a configured MSI to configure the agent.");
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = [],
    ContentRootPath = AppContext.BaseDirectory
});
builder.Services.AddWindowsService(settings => settings.ServiceName = AgentOptions.WindowsServiceName);

// Connection settings come exclusively from protected installed state and MSI enrollment.
// Host configuration (JSON, environment, user secrets and command line) cannot override them.
var options = new AgentOptions
{
    StateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CloneDeploy", "Agent"),
    DeviceName = Environment.MachineName
};
using var store = new AgentStore(options.StateDirectory);
ImagePrepEnrollment.Import(store, options);
options.ServerUrl = store.GetSetting("ServerUrl") ?? throw new InvalidOperationException("Install a configured agent MSI before starting the agent.");
options.ProvisioningKey = store.GetProvisioningKey() ?? "";
options.ServerCaCertificatePath = store.GetSetting("ServerCaCertificatePath");

using var fileLog = new AgentFileLoggerProvider(options);
builder.Logging.AddProvider(fileLog);
if (command is not null)
{
    store.ResetIdentity(options, fullReset: command.Equals("--resetfull", StringComparison.OrdinalIgnoreCase));
    Console.WriteLine("Enrollment reset. Remove the reset argument, start the agent, and approve its reset request under Devices.");
    return;
}

store.SaveConfiguration(options);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(store);
builder.Services.AddSingleton<AgentIdentity>();
builder.Services.AddSingleton<AgentAdminSession>();
builder.Services.AddSingleton<AgentWorkAccount>();
builder.Services.AddSingleton<AgentDriverPackTool>();
builder.Services.AddSingleton<AgentUsbTool>();
builder.Services.AddSingleton<AgentImagePrepTool>(sp => new(sp.GetRequiredService<AgentOptions>(),
    sp.GetRequiredService<AgentAdminSession>(), sp.GetRequiredService<IPolicyProcess>(), sp.GetRequiredService<ILogger<AgentImagePrepTool>>())
    { ImagingBoot = sp.GetRequiredService<AgentImagingBoot>() });
builder.Services.AddSingleton<UserLoginJournal>();
builder.Services.AddSingleton<UserLoginTracker>();
builder.Services.AddHostedService(services => services.GetRequiredService<UserLoginTracker>());
builder.Services.AddSingleton<AgentClient>(services => new AgentClient(
    options,
    services.GetRequiredService<AgentIdentity>())
{
    UserLogins = services.GetRequiredService<UserLoginJournal>(),
    LoginTracker = services.GetRequiredService<UserLoginTracker>()
});
builder.Services.AddSingleton<InventoryCollector>();
builder.Services.AddSingleton<AgentInventoryHandler>();
builder.Services.AddSingleton<AgentTaskSignal>();
builder.Services.AddSingleton<AgentActivityTracker>();
builder.Services.AddHostedService<AgentActivityWorker>();
builder.Services.AddSingleton<DeviceTaskJournal>();
builder.Services.AddSingleton<AgentTaskRunner>();
builder.Services.AddHostedService<AgentTaskWorker>();
builder.Services.AddSingleton<AgentImagingBoot>();
builder.Services.AddHostedService(services => services.GetRequiredService<AgentImagingBoot>());
builder.Services.AddSingleton<AgentRealtimeClient>(services => new AgentRealtimeClient(
    options,
    services.GetRequiredService<AgentIdentity>(),
    services.GetRequiredService<ILogger<AgentRealtimeClient>>(),
    services.GetRequiredService<AgentInventoryHandler>(),
    services.GetRequiredService<AgentActivityTracker>())
{
    ImagingBoot = services.GetRequiredService<AgentImagingBoot>(),
    TaskSignal = services.GetRequiredService<AgentTaskSignal>(),
    FileLog = fileLog
});
builder.Services.AddSingleton<PolicyJournal>();
builder.Services.AddSingleton<PortalNotificationJournal>();
builder.Services.AddHostedService<PortalNotificationReporter>();
builder.Services.AddSingleton<PolicyCache>();
builder.Services.AddSingleton<IPolicyProcess, PolicyProcess>();
builder.Services.AddSingleton<IRunAsWindows, RunAsWindows>();
builder.Services.AddSingleton<ScheduledRunAsProcess>();
builder.Services.AddSingleton<PolicyActionRunner>(services => new PolicyActionRunner(services.GetRequiredService<IPolicyProcess>())
{
    RunAs = services.GetRequiredService<ScheduledRunAsProcess>(),
    Notifications = services.GetRequiredService<PortalNotificationJournal>()
});
builder.Services.AddSingleton<AgentPolicyRunner>();
builder.Services.AddHostedService(sp => new LoginPolicyService(sp.GetRequiredService<AgentPolicyRunner>(), sp.GetRequiredService<ILogger<LoginPolicyService>>())
{ WorkAccount = sp.GetRequiredService<AgentWorkAccount>() });
builder.Services.AddSingleton<AgentUpdater>();
builder.Services.AddHostedService(services => new AgentPortalService(
        services.GetRequiredService<AgentClient>(),
        options,
        services.GetRequiredService<ILogger<AgentPortalService>>())
    {
        Notifications = services.GetRequiredService<PortalNotificationJournal>(),
        RemoteConsent = services.GetRequiredService<AgentRealtimeClient>().RemoteConsent,
        HelpPresence = services.GetRequiredService<AgentRealtimeClient>().HelpPresence,
        Admin = services.GetRequiredService<AgentAdminSession>(),
        WorkAccount = services.GetRequiredService<AgentWorkAccount>(),
        DriverPacks = services.GetRequiredService<AgentDriverPackTool>(),
        Usb = services.GetRequiredService<AgentUsbTool>(),
        ImagePrep = services.GetRequiredService<AgentImagePrepTool>()
    });
builder.Services.AddHostedService(services => new Worker(
    services.GetRequiredService<AgentTaskRunner>(),
    services.GetRequiredService<AgentPolicyRunner>(),
    services.GetRequiredService<AgentClient>(),
    services.GetRequiredService<AgentRealtimeClient>(),
    options,
    services.GetRequiredService<IHostApplicationLifetime>(),
    services.GetRequiredService<ILogger<Worker>>(),
    services.GetRequiredService<AgentActivityTracker>())
{
    Updater = services.GetRequiredService<AgentUpdater>()
});

using var host = builder.Build();
host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Agent")
    .LogInformation(
        $"Agent {typeof(Worker).Assembly.GetName().Version} starting as a Windows service. Server: {options.GetServerUri()}. Device name: {options.DeviceName}. Log file: {fileLog.LogPath}");
try
{
    await host.Services.GetRequiredService<ScheduledRunAsProcess>().RecoverAsync();
}
catch (Exception ex)
{
    host.Services.GetRequiredService<ILogger<ScheduledRunAsProcess>>()
        .LogError(
            $"Run-as recovery needs attention: {(ex is InvalidOperationException or TimeoutException ? ex.Message : ex.GetType().Name)}. Credential-based execution will remain blocked until cleanup succeeds.");
}

host.Run();
