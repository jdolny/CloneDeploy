using System.Runtime.InteropServices;
using CloneDeploy_Agent.MacOS;

if (args.Length == 1 && args[0] == "--version") { Console.WriteLine(MacUpdater.InstalledVersion); return 0; }
if (args is ["--help"])
{
    Console.WriteLine("--help, --version, --status, --reset (keep installation ID), --resetfull (new installation ID). Normal execution requires the installed launchd daemon. Install a configured package to change settings.");
    return 0;
}
if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
{
    Console.Error.WriteLine("This agent requires macOS on Apple Silicon, running natively as arm64.");
    return 1;
}
if (Native.geteuid() != 0) { Console.Error.WriteLine("Run the agent with sudo or as its installed system daemon."); return 1; }
if (args.Length == 2 && args[0] == "--terminal-child")
{ MacDaemonGuard.RequireTerminalParent(); return MacPseudoTerminal.RunChild(args[1]); }
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; stop.Cancel(); });
try
{
    if (args.Length > 1 || (args.Length == 1 && args[0] is not ("--status" or "--reset" or "--resetfull" or "--import-package" or "--check-state-lock")))
        throw new ArgumentException("Unsupported agent argument. Use --help.");
    var command = args.FirstOrDefault();
    if (command is null) MacDaemonGuard.RequireDaemon();
    if (command is "--import-package" or "--check-state-lock" && Environment.ProcessPath != MacDaemonGuard.Executable)
        throw new UnauthorizedAccessException("Installer helpers require the installed executable.");
    using var store = new MacStateStore(MacStateStore.DefaultDirectory, readOnly: command == "--status");
    if (command == "--check-state-lock") return 0;
    if (command == "--import-package")
    {
        var prepared = Path.Combine(MacStateStore.DefaultDirectory, "bin", "enrollment.json");
        if (File.Exists(prepared))
        {
            store.ConfigurePackage(prepared);
            File.Delete(prepared); // Consume only after committing configuration and identity.
        }
        else if (store.Read<MacConfiguration>("configuration.json") is null)
            throw new InvalidOperationException("Install a configured package for initial enrollment.");
        return 0;
    }
    var config = (store.Read<MacConfiguration>("configuration.json") ?? throw new InvalidOperationException("Install a configured agent package first."))
        with { DeviceName = Environment.MachineName };
    if (command == "--status")
    {
        var state = store.Read<MacIdentityState>("identity.json");
        Console.WriteLine($"Server: {config.ServerUrl}\nDevice: {config.DeviceName}\nInstallation: {state?.InstallationId.ToString() ?? "not created"}\nDevice ID: {state?.DeviceGuid?.ToString() ?? "not enrolled"}\nReset requested: {state?.RequestReset ?? false}");
        return 0;
    }
    if (command is "--reset" or "--resetfull")
    {
        store.ResetEnrollment(command == "--resetfull");
        Console.WriteLine("A new key has been created. The next enrollment attempt will request a server-approved identity reset.");
        return 0;
    }
    if (command is null && store.DirectoryPath == MacStateStore.DefaultDirectory &&
        Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory) == Path.Combine(MacStateStore.DefaultDirectory, "bin"))
        await MacPortalInstaller.EnsureAsync(stop.Token);
    using var identity = new MacIdentity(store, config);
    string? uuid = null;
    try { uuid = await MacHardware.ReadUuidAsync(stop.Token); }
    catch (Exception ex) when (!stop.IsCancellationRequested && ex is IOException or System.Xml.XmlException or OperationCanceledException or System.ComponentModel.Win32Exception)
    { Console.Error.WriteLine("Hardware UUID unavailable; enrollment will use the installation identity and available MAC addresses."); }
    var userLogins = new CloneDeploy_Agent.UserLoginJournal(config.Options(store.DirectoryPath));
    var loginTracker = new MacLoginTracker(userLogins);
    var deviceTools = new MacDeviceTools();
    using var client = new MacEnrollmentClient(config, identity, store.DirectoryPath, uuid) { UserLogins = userLogins, LoginTracker = loginTracker };
    var activity = new MacActivityTracker();
    using var executionGate = new SemaphoreSlim(1, 1);
    var taskSignal = new MacTaskSignal();
    var collector = new MacInventoryCollector();
    var inventory = new MacInventoryHandler(config, identity, store.DirectoryPath, collector.CollectAsync, stop.Token) { Activity = activity };
    var notifications = new CloneDeploy_Agent.PortalNotificationJournal(config.Options(store.DirectoryPath));
    var policies = new MacPolicyRunner(config, identity, store.DirectoryPath, inventory) { Notifications = notifications, ExecutionGate = executionGate, Activity = activity, DeviceTools = deviceTools, LoginTracker = loginTracker };
    var workIdentity = new MacWorkIdentity(config, identity, store.DirectoryPath);
    var helpPresence = new MacHelpPresence();
    await using var remote = new MacRemoteDesktop(config, identity, store.DirectoryPath);
    await using var live = new MacRealtimeClient(config, identity, store.DirectoryPath) { DeviceTools = deviceTools, Inventory = inventory, HelpPresence = helpPresence, Remote = remote, Activity = activity, Tasks = taskSignal };
    using var liveStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
    Task? liveTask = null;
    var taskRunner = new MacTaskRunner(config, identity, store.DirectoryPath, notifications, executionGate, activity);
    var loginWorker = command is null ? loginTracker.RunAsync(liveStop.Token) : Task.CompletedTask;
    var taskWorker = command is null ? new MacTaskWorker(taskRunner, taskSignal).RunAsync(liveStop.Token) : Task.CompletedTask;
    var userPolicyWorker = command is null ? workIdentity.RunPoliciesAsync(policies, liveStop.Token) : Task.CompletedTask;
    var activityWorker = command is null ? live.RunActivityAsync(liveStop.Token) : Task.CompletedTask;
    var portalStatus = new MacPortalStatus { DeviceName = config.DeviceName, Version = MacUpdater.InstalledVersion };
    var portal = new MacPortalServer(() => Volatile.Read(ref portalStatus) with { Connected = live.IsConnected, Branding = client.Branding, Features = client.Features });
    var portalTask = command is null && store.DirectoryPath == MacStateStore.DefaultDirectory
        ? portal.RunAsync(liveStop.Token) : Task.CompletedTask;
    var selfService = new MacSelfService(config, identity, store.DirectoryPath, policies, liveStop.Token);
    var portalAuth = new MacPortalAuthentication(config, identity, store.DirectoryPath) { SelfService = selfService, HelpPresence = helpPresence, Remote = remote, Notifications = notifications, WorkIdentity = workIdentity };
    var notificationTask = command is null ? new MacNotificationReporter(config, identity, store.DirectoryPath, notifications).RunAsync(liveStop.Token) : Task.CompletedTask;
    var portalAuthTask = command is null && store.DirectoryPath == MacStateStore.DefaultDirectory
        ? portalAuth.RunAsync(liveStop.Token) : Task.CompletedTask;
    var remoteIpcTask = command is null && store.DirectoryPath == MacStateStore.DefaultDirectory
        ? remote.RunIpcAsync(liveStop.Token) : Task.CompletedTask;
    var updater = new MacUpdater(config, identity, store);
    var startup = true;
    try
    {
        while (!stop.IsCancellationRequested)
        {
            TimeSpan retry;
            try
            {
                MacCycleResult result;
                using (activity.Begin(CloneDeploy_Agent.Contracts.AgentActivityKind.CheckingIn))
                    result = await client.RunOnceAsync(stop.Token);
                Volatile.Write(ref portalStatus, portalStatus with { Enrollment = result.Status,
                    LastCheckinUtc = result.Status == "CheckedIn" ? DateTimeOffset.UtcNow : portalStatus.LastCheckinUtc });
                Console.WriteLine($"{DateTimeOffset.UtcNow:O} {result.Status}; next attempt in {result.RetryAfter.TotalSeconds:0}s{(result.DeviceGuid is { } id ? $"; device {id}" : "")}");
                if (result.Status == "CheckedIn")
                {
                    MacUpdater.MarkHealthy(store);
                    taskSignal.Request(true);
                    liveTask ??= live.RunAsync(liveStop.Token);
                    try { await updater.CheckAsync(result.Update, stop.Token); }
                    catch (Exception ex) when (!stop.IsCancellationRequested)
                    { Console.Error.WriteLine($"{DateTimeOffset.UtcNow:O} Mac update failed ({ex.GetType().Name}): {ex.Message}"); }
                }
                retry = result.RetryAfter;
                if (result.Status == "CheckedIn")
                {
                    if (result.Update is null || CloneDeploy_Agent.Contracts.AgentUpdateOffer.ParseVersion(result.Update.Version) == CloneDeploy_Agent.Contracts.AgentUpdateOffer.ParseVersion(MacUpdater.InstalledVersion))
                    {
                        await policies.RunAsync((int)(startup ? CloneDeploy_Agent.Contracts.PolicyTrigger.Startup : CloneDeploy_Agent.Contracts.PolicyTrigger.Checkin), stop.Token);
                        startup = false;
                    }
                    else
                    {
                        Console.WriteLine($"Policies are waiting for the installed agent ({MacUpdater.InstalledVersion}) to match the prepared Mac release ({result.Update.Version}).");
                        retry = TimeSpan.FromSeconds(Math.Min(60, retry.TotalSeconds));
                    }
                }
            }
            catch (Exception ex) when (!stop.IsCancellationRequested && ex is HttpRequestException or IOException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException or OperationCanceledException)
            {
                Console.Error.WriteLine($"{DateTimeOffset.UtcNow:O} {ex.Message}");
                retry = TimeSpan.FromSeconds(60);
            }
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            wait.CancelAfter(retry + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 3000)));
            try { await live.CheckinSignal.WaitAsync(wait.Token); }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested) { }
        }
    }
    finally
    {
        liveStop.Cancel();
        if (liveTask is not null) await liveTask;
        await taskWorker;
        await loginWorker;
        await activityWorker;
        await userPolicyWorker;
        await portalTask;
        await portalAuthTask;
        await selfService.Completion;
        await notificationTask;
        await remoteIpcTask;
    }
    return 0;
}
catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }

internal static class Native
{
    [DllImport("libc")] internal static extern uint geteuid();
}
