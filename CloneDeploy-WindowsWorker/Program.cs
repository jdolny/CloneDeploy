using CloneDeploy.SiteService;
using CloneDeploy.SiteServices;

ServiceHostBoundary.AssertWindowsWorker();
var directory = Environment.GetEnvironmentVariable("CLONEDEPLOY_WORKER_DATA") ??
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CloneDeploy-WindowsWorker");
var store = new SiteLocalStore(directory, true);
if (args is ["--import-enrollment", var setup]) { SiteEnrollmentClient.Import(setup, store); return; }
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Configuration.AddJsonFile("worker.json", optional: true).AddEnvironmentVariables().AddCommandLine(args);
builder.Services.AddWindowsService(o => o.ServiceName = "CloneDeploy-WindowsWorker");
builder.Services.AddSingleton(store);
builder.Services.AddSingleton<WindowsWorkEngine>();
builder.Services.AddSingleton<WindowsWorkWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WindowsWorkWorker>());
builder.Services.AddHostedService<ManagedWindowsWorker>();
await builder.Build().RunAsync();
