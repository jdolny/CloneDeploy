using CloneDeploy_Server.WinPe;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Services.AddWindowsService(options => options.ServiceName = "CloneDeploy WinPE Builder");
var options = builder.Configuration.GetSection("WinPe").Get<WinPeOptions>() ?? new();
if (!Path.IsPathFullyQualified(options.RootPath))
    throw new InvalidOperationException("Configure WinPe:RootPath as the absolute path to the web application's WinPE storage directory.");
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(new BuildStorage(options.RootPath) { SecureWrites = true, QueueWriterSid = options.QueueWriterSid });
builder.Services.AddSingleton<ProtectedBuildRunner>();
builder.Services.AddHostedService<BuilderWorker>();
await builder.Build().RunAsync();
