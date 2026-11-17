using CloneDeploy.SiteService;

CloneDeploy.SiteServices.ServiceHostBoundary.AssertSiteService();
var paths = SiteRuntimePaths.Resolve();

if (args is ["--import-enrollment", var setupPath])
{
    SiteEnrollmentClient.Import(setupPath, new SiteLocalStore(paths.DataRoot, false));
    return;
}
if (args.Any(x => x == "--create-identity"))
    throw new InvalidOperationException("Standalone proxy identities are no longer supported. Create a Site Service registration on the main server and import its setup JSON with --import-enrollment.");

if (!File.Exists(Path.Combine(paths.DataRoot, "enrollment.protected")))
    throw new InvalidOperationException($"Site Service enrollment is required. No enrollment.protected was found in '{paths.DataRoot}'. Create a registration in Admin Settings > Site Services and select its setup JSON in the installer, or run CloneDeploy-SiteService --import-enrollment <setup.json> using the service's data directory. Standalone proxy configuration is no longer supported.");

var managed = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
managed.Configuration.AddJsonFile(paths.ConfigurationFile, optional: !OperatingSystem.IsLinux(), reloadOnChange: false).AddEnvironmentVariables().AddCommandLine(args);
Directory.SetCurrentDirectory(paths.DataRoot);
managed.Services.AddSingleton(paths);
managed.Services.AddWindowsService(settings => settings.ServiceName = "CloneDeploy-SiteService");
managed.Services.AddSingleton(new SiteLocalStore(paths.DataRoot));
managed.Services.AddSingleton(TimeProvider.System);
managed.Services.AddSingleton<SiteBootCache>();
managed.Services.AddSingleton<SiteBootWorker>();
managed.Services.AddHostedService(sp => sp.GetRequiredService<SiteBootWorker>());
managed.Services.AddSingleton<SiteStorageCache>();
managed.Services.AddSingleton<SiteStorageWorker>();
managed.Services.AddHostedService(sp => sp.GetRequiredService<SiteStorageWorker>());
managed.Services.AddSingleton<SiteMulticastWorker>();
managed.Services.AddHostedService(sp => sp.GetRequiredService<SiteMulticastWorker>());
managed.Services.AddHostedService<ManagedSiteService>(sp => new(sp.GetRequiredService<SiteLocalStore>(), sp.GetRequiredService<ILogger<ManagedSiteService>>(),
    sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<SiteBootWorker>()) { BrowserConfiguration = managed.Configuration, Multicast = sp.GetRequiredService<SiteMulticastWorker>(), Storage = sp.GetRequiredService<SiteStorageWorker>(), StorageCache = sp.GetRequiredService<SiteStorageCache>() });
await managed.Build().RunAsync();
