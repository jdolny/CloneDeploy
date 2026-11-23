using System.Globalization;
using CloneDeploy_Server.Backend;
using CloneDeploy.SiteServices;
using CloneDeploy_Server.Backend.Auth;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Services;
using CloneDeploy_Server.Backend.Services.Files;
using CloneDeploy_Server.Backend.Services.Scheduler;
using CloneDeploy_Server.UI;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using MudBlazor;
using MudBlazor.Services;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()                     // temporary bootstrap logger
    .CreateBootstrapLogger();

string? startupDataDirectory = null;

try
{
    Log.Information("Starting application");

    var development = string.Equals(Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ??
        Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase);
    string? installationData;
    string? configurationFile = null;
    if (OperatingSystem.IsLinux() && !development)
    {
        startupDataDirectory = ServerRuntimePaths.DefaultLinuxDataRoot;
        configurationFile = ServerRuntimePaths.LinuxConfigurationFile;
        using var bootstrap = new ConfigurationManager();
        bootstrap.AddJsonFile(configurationFile, optional: false, reloadOnChange: false);
        installationData = ServerRuntimePaths.ReadLinuxDataRoot(bootstrap, AppContext.BaseDirectory);
    }
    else if (OperatingSystem.IsWindows() && !development)
        installationData = ServerRuntimePaths.WindowsDataRoot;
    else
        installationData = Environment.GetEnvironmentVariable("CLONEDEPLOY_DATA"); // Isolated development/test launches only.
    if (!string.IsNullOrWhiteSpace(installationData))
    {
        startupDataDirectory = Path.GetFullPath(installationData);
        if (!Directory.Exists(installationData))
            throw new DirectoryNotFoundException($"Server data directory does not exist: {installationData}. Create it and grant the server service account access before starting.");
        Directory.SetCurrentDirectory(installationData);
        configurationFile ??= Path.Combine(installationData, "server.json");
    }

    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = installationData,
        WebRootPath = string.IsNullOrWhiteSpace(installationData)
            ? (Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), "UI", "wwwroot")) ? "UI/wwwroot" : "wwwroot")
            : Path.Combine(AppContext.BaseDirectory, "wwwroot")
    });
    if (!string.IsNullOrWhiteSpace(installationData))
    {
        builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false)
            .AddJsonFile(configurationFile!, optional: false, reloadOnChange: false)
            .AddEnvironmentVariables()
            .AddCommandLine(args);
    }
    else
    {
        // Keep administrator overrides separate from files replaced by a publish.
        // Re-add explicit overrides so server.json never takes precedence over them.
        builder.Configuration.AddJsonFile("server.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .AddCommandLine(args);
    }
    builder.Services.AddSingleton(new ServerRuntimePaths(builder.Environment.ContentRootPath,
        configurationFile ?? Path.Combine(builder.Environment.ContentRootPath, "server.json")));
    builder.Services.AddWindowsService(options => options.ServiceName = "CloneDeploy-Server");
    // Register before workers/listeners so SCM is connected before migrations begin,
    // while application work still waits for database initialization to finish.
    builder.Services.AddHostedService<ServerStartup>();
    builder.Services.AddSingleton<ProfessionalSupport>();
    builder.Services.AddScoped<InstallationWelcome>();
    builder.Services.AddScoped<AccountMfaSetup>();
    builder.Services.AddScoped<AccountMfaReset>();
    builder.Services.AddScoped<PolicyReportService>();
    builder.Services.AddHostedService<ProfessionalSupportWorker>();
    builder.Services.AddHttpClient(ProfessionalSupport.ClientName, client =>
    {
        client.Timeout = TimeSpan.FromSeconds(15);
        client.MaxResponseContentBufferSize = 16 * 1024;
    }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
    builder.Services.AddHostedService<AgentReleaseStartup>();
    builder.AddAgentApi();
    builder.AddServerListeners();
    builder.Services.AddTrustedSiteProxies();
    builder.Services.AddScoped<SiteServiceAdministration>();
    builder.Services.AddSingleton<SiteBootFiles>();
    builder.Services.AddScoped<SiteServiceEnrollmentService>();
    builder.Services.AddScoped<ISiteProxyTrust>(services => services.GetRequiredService<SiteServiceEnrollmentService>());
    builder.Services.AddRateLimiter(options => options.AddPolicy("SiteEnrollment", _ =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter("site-enrollment", _ =>
            new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));
    builder.Services.AddImagingApi();
    
    var dataProtection = builder.Services
        .AddDataProtection()
        .PersistKeysToFileSystem(
            new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "keys")));
    CloneDeploy_Server.Backend.Services.Security.Secrets.ServerSecretConfiguration.Configure(
        builder.Configuration, dataProtection,
        required: OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(installationData));
    
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

// Add MudBlazor services
    builder.Services.AddMudServices();
    builder.Services.AddLocalizationEnumInterceptor<ComponentEnumLocalizer>();
    builder.Services.AddMudBlazorSnackbar(config =>
    {
        config.PositionClass = Defaults.Classes.Position.TopCenter;
        config.PreventDuplicates = true;
        config.NewestOnTop = true;
        config.ShowCloseIcon = true;
        config.VisibleStateDuration = 4000;
        config.HideTransitionDuration = 500;
        config.ShowTransitionDuration = 500;
        config.ClearAfterNavigation = false;
        config.SnackbarVariant = Variant.Filled;
    });

    var connectionString = builder.Configuration.GetConnectionString("CloneDeploy-Server-Database");
    builder.Services.AddDbContext<CloneDeployDbContext>(options =>
        options.UseNpgsql(connectionString));
    builder.Services.AddDbContextFactory<CloneDeployDbContext>(options =>
        options.UseNpgsql(connectionString), ServiceLifetime.Scoped);

    var authenticationSettings = AuthenticationSettings.Read(builder.Configuration);
    builder.Services.AddIdentity<AppUser, IdentityRole<int>>(options =>
        {
            options.SignIn.RequireConfirmedAccount = false;
            options.User.RequireUniqueEmail = false;
            options.Password = authenticationSettings.Password;
            options.Lockout = authenticationSettings.Lockout;
            options.Tokens.AuthenticatorTokenProvider = TokenOptions.DefaultAuthenticatorProvider;
        })
        .AddEntityFrameworkStores<CloneDeployDbContext>()
        .AddErrorDescriber<LocalizedIdentityErrors>()
        .AddDefaultTokenProviders();
    
    builder.Services.AddScoped<
        IUserClaimsPrincipalFactory<AppUser>, AppUserClaimsPrincipalFactory>();

    builder.Services.Configure<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>(
        IdentityConstants.TwoFactorRememberMeScheme, BrowserSession.DisableRememberedMfa);
    builder.Services.ConfigureApplicationCookie(options =>
    {
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/access-denied";
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(1);
        BrowserSession.ConfigureCookie(options);
    });
    builder.Services.Configure<SecurityStampValidatorOptions>(options =>
    {
        options.ValidationInterval = TimeSpan.Zero;
    });

    var applicationPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .AddRequirements(new MfaEnrollmentRequirement(), new PermissionRequirement("Directory.ApplicationAccess"))
        .Build();
    
    var authorization = builder.Services.AddAuthorizationBuilder()
        .AddPolicy(Constants.Permissions.TaskOwnAccess, applicationPolicy)
        .SetDefaultPolicy(applicationPolicy)
        .SetFallbackPolicy(applicationPolicy)
        .AddPolicy("SignedIn", policy =>
            policy.RequireAuthenticatedUser().AddRequirements(new PermissionRequirement("Directory.ApplicationAccess")))
        .AddPolicy(Constants.Permissions.AdminOnly, policy => policy
            .RequireAuthenticatedUser()
            .AddRequirements(new MfaEnrollmentRequirement(), new PermissionRequirement("Directory.ApplicationAccess"))
            .AddRequirements(new PermissionRequirement(Constants.Permissions.AdminOnly)));

    foreach (var permission in Constants.Permissions.All.Append(new(Constants.Permissions.HelpDeskAccess, "Help Desk", "Access Help Desk")))
    {
        authorization.AddPolicy(permission.Key, policy => policy
            .RequireAuthenticatedUser()
            .AddRequirements(
                new MfaEnrollmentRequirement(),
                new PermissionRequirement(permission.Key)));
    }
    
    builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, MfaAuthorizationResultHandler>();

// Add services to the container.
    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents()
        .AddHubOptions(options => options.MaximumReceiveMessageSize = 1024 * 1024);
    builder.Services.AddCascadingAuthenticationState();
    // MVC antiforgery attributes require the view-feature filter services.
    builder.Services.AddControllersWithViews(options => options.Filters.Add<SiteDownloadResultFilter>());
    builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
    builder.Services.AddOptions<UploadOptions>()
        .BindConfiguration("Uploads")
        .PostConfigure(options =>
        {
            options.StoragePath = ServerDataPaths.UploadsRelative;
            options.TempStoragePath = ServerDataPaths.UploadStagingRelative;
        })
        .Validate(options => options.MaxFileSize > 0 && options.MaxActiveUploads > 0 &&
                             !string.IsNullOrWhiteSpace(options.StoragePath) &&
                             !string.IsNullOrWhiteSpace(options.TempStoragePath), "Invalid upload configuration")
        .ValidateOnStart();
    builder.Services.AddSingleton<ChunkedUploadStore>();
    builder.Services.AddScoped<FileService>();
    builder.Services.AddScoped<ActionFileDownloadService>(sp => new(sp.GetRequiredService<ICloneDeployDbFactory>(), sp.GetRequiredService<StoredFileStorage>(), sp.GetRequiredService<ILogger<ActionFileDownloadService>>()) { Shared = sp.GetRequiredService<SharedContentDelivery>() });
    builder.Services.AddScoped<DriverFileDownloadService>(sp => new(sp.GetRequiredService<ICloneDeployDbFactory>(), sp.GetRequiredService<StoredFileStorage>(),
        sp.GetRequiredService<DriverPreparationStorage>(), sp.GetRequiredService<ILogger<DriverFileDownloadService>>()) { Shared = sp.GetRequiredService<SharedContentDelivery>() });
    builder.Services.AddScoped<WinPeDownloadService>();
    builder.Services.AddScoped<WinPeSetupDownloadService>();
    builder.Services.AddHostedService(provider => provider.GetRequiredService<ChunkedUploadStore>());
    builder.Services.AddSingleton<StoredFileStorage>();
    builder.Services.AddScoped<ActionFileService>();
    builder.Services.AddHttpClient("ExternalFiles", client => client.Timeout = TimeSpan.FromMinutes(2))
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseCookies = false });
    builder.Services.AddHostedService<ExternalDownloadWorker>();

    builder.Services.AddScoped<ICloneDeployDbFactory, CloneDeployDbFactory>();
    builder.Services.AddScoped<TftpSettingsStore>();
    builder.Services.AddScoped<TftpSettingsAdministration>();
    builder.Services.AddScoped<TftpService>();
    builder.Services.AddSingleton<TftpRootDirectory>();
    builder.Services.AddSingleton<TftpWorker>();
    builder.Services.AddHostedService(provider => provider.GetRequiredService<TftpWorker>());
    builder.Services.AddScoped<ServerUrlSettingsStore>();
    builder.Services.AddScoped<ServerSettingsService>();
    builder.Services.AddScoped<ServerFeatureSettings>();
    builder.Services.AddSingleton<DatabaseBackupService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<DatabaseBackupService>());
    builder.Services.AddScoped<PxeSettingsStore>();
    builder.Services.AddScoped<PxeSettingsAdministration>();
    builder.Services.AddScoped<PxeService>();
    builder.Services.AddScoped<PxeBootFilesService>();
    builder.Services.AddSingleton<PxeBootFileManager>();
    builder.Services.AddHttpClient("PxeBootFiles", client =>
    {
        client.Timeout = TimeSpan.FromMinutes(5);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CloneDeploy-Server/1.0");
    }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
    builder.Services.AddSingleton<NativePxeChanges>();
    builder.Services.AddSingleton<NativePxeReservations>();
    builder.Services.AddSingleton<NativePxePublication>();
    builder.Services.AddHostedService<NativePxeReservationWorker>();
    builder.Services.AddSingleton<PxeWorker>();
    builder.Services.AddHostedService(provider => provider.GetRequiredService<PxeWorker>());
    builder.Services.AddScoped<DeviceService>();
    builder.Services.AddScoped<DeviceActivityService>();
    builder.Services.AddScoped<DeviceReportService>();
    builder.Services.AddScoped<DeviceReportBuilderService>();
    builder.Services.AddScoped<UserDeviceReportService>();
    builder.Services.AddScoped<SoftwareReportService>();
    builder.Services.AddScoped<DeviceStatsService>();
    builder.Services.AddScoped<EmailNotificationService>();
    builder.Services.AddScoped<IEmailTransport, SmtpEmailTransport>();
    builder.Services.AddScoped(sp => new EmailDigestBuilder(sp.GetRequiredService<ICloneDeployDbFactory>(),
        sp.GetRequiredService<AppUserPermissionService>(), sp.GetRequiredService<ServerUrlSettingsStore>())
        { Updates = sp.GetRequiredService<ServerUpdateChecker>(), Certificates = sp.GetRequiredService<CertificateExpiryNotices>(), Support = sp.GetRequiredService<ProfessionalSupport>() });
    builder.Services.AddScoped<CertificateExpiryNotices>();
    builder.Services.AddScoped<EmailDigestRunner>();
    builder.Services.AddScoped<IScheduledTaskHandler, EmailDigestTaskHandler>();
    builder.Services.AddScoped<DeviceAttachmentService>();
    builder.Services.AddScoped<DeviceAdministration>();
    builder.Services.AddScoped<DashboardService>();
    builder.Services.AddSingleton(RunningRuntimeVersions.Read());
    builder.Services.AddSingleton<RuntimeUpdateChecker>();
    builder.Services.AddHostedService<RuntimeUpdateWorker>();
    builder.Services.AddHttpClient(RuntimeUpdateChecker.ClientName, client =>
    {
        client.Timeout = TimeSpan.FromSeconds(15);
        client.MaxResponseContentBufferSize = 8 * 1024 * 1024;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CloneDeploy-Server/1.0");
    }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
    builder.Services.AddScoped<SecurityActivity>();
    builder.Services.AddSingleton<ServerUpdateChecker>();
    builder.Services.AddHttpClient(ServerUpdateChecker.ClientName, client =>
    {
        client.Timeout = TimeSpan.FromSeconds(15);
        client.MaxResponseContentBufferSize = 1024 * 1024;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CloneDeploy-Server/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
    }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
    builder.Services.AddScoped<DashboardQueries>();
    builder.Services.AddScoped<DeviceColumnPreferencesService>();
    builder.Services.AddScoped<CloneDeploy_Server.Backend.Services.DynamicGroups.DynamicDeviceGroupService>();
    builder.Services.AddScoped<CloneDeploy_Server.Backend.Services.DynamicGroups.DynamicDeviceGroupRunner>();
    builder.Services.AddScoped<IActiveDirectoryReader, LdapActiveDirectoryReader>();
    builder.Services.AddScoped<ActiveDirectoryService>();
    builder.Services.AddScoped<DirectoryService>();
    builder.Services.AddScoped<DirectorySourceService>();
    builder.Services.AddScoped<DirectorySyncRunner>();
    builder.Services.AddScoped<IDirectorySyncProvider, ActiveDirectorySyncProvider>();
    builder.Services.AddScoped<IDirectorySyncProvider, EntraDirectorySyncProvider>();
    builder.Services.AddScoped<EntraCredentials>();
    builder.Services.AddScoped<IEntraTokenProvider, EntraCredentials>();
    builder.Services.AddScoped<EntraAdministration>();
    builder.Services.AddHttpClient<EntraGraphReader>(client => { client.Timeout = TimeSpan.FromSeconds(60); client.MaxResponseContentBufferSize = 16 * 1024 * 1024; })
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
    builder.Services.AddEntraAuthentication();
    builder.Services.AddScoped<NavigationSearchService>();
    builder.Services.AddScoped<NavigationSearchQueries>();
    builder.Services.AddScoped<TagService>();
    builder.Services.AddScoped<SharedResourceAccess>();
    builder.Services.AddScoped<TagAdministration>();
    builder.Services.AddScoped<DeviceGroupAdministration>();
    builder.Services.AddScoped<DeviceGroupService>();
    builder.Services.AddScoped<ScheduleService>();
    builder.Services.AddScoped<ScheduleAdministration>();
    builder.Services.AddSingleton<ScheduleCalculator>();
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddScoped<HelpDeskService>();
    builder.Services.AddScoped<HelpDeskReportService>();
    builder.Services.AddScoped<HelpRoutingService>();
    builder.Services.AddScoped<HelpDeskScope>();
    builder.Services.AddScoped<HelpEmailRunner>();
    builder.Services.AddHostedService<HelpEmailWorker>();
    builder.Services.AddSingleton<SchedulerGate>();
    builder.Services.AddScoped<SchedulerQueue>();
    builder.Services.AddScoped<SchedulerService>();
    builder.Services.AddScoped<SchedulerAdministration>();
    builder.Services.AddScoped<SchedulerQueueProcessor>();
    builder.Services.AddScoped<IScheduledTaskExecutor, ScheduledTaskExecutor>();
    builder.Services.AddScoped<IScheduledTaskHandler, DirectorySyncTaskHandler>();
    builder.Services.AddScoped<IScheduledTaskHandler, DynamicGroupTaskHandler>();
    builder.Services.AddScoped<IScheduledTaskHandler, GroupPowerScheduleTaskHandler>();
    builder.Services.AddScoped<GroupPowerScheduleService>();
    builder.Services.AddScoped<IScheduledPowerActions, ScheduledPowerActions>();
    builder.Services.AddScoped<ExternalFileRefreshTaskHandler>();
    builder.Services.AddScoped<IScheduledTaskHandler, ScheduledExternalFilesHandler>();
    builder.Services.AddScoped<IScheduledTaskHandler, WinGetManifestImportTaskHandler>();
    builder.Services.AddScoped<WinGetSettingsService>();
    builder.Services.AddScoped<WinGetService>();
    builder.Services.AddScoped<WinGetCatalogService>();
    builder.Services.AddScoped<WinGetManifestImporter>();
    builder.Services.AddHttpClient("WinGetManifests", client =>
    {
        client.Timeout = TimeSpan.FromMinutes(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CloneDeploy-Server/1.0");
    }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseCookies = false });
    builder.Services.AddHostedService<SchedulerHostedService>();
    builder.Services.AddScoped<ScheduledTaskExecutor>();
    builder.Services.AddScoped<CustomAttributeService>();
    builder.Services.AddScoped<CustomAttributeAdministration>();
    builder.Services.AddScoped<ManagedCredentialService>();
    builder.Services.AddScoped<ManagedCredentialAdministration>();
    builder.Services.AddScoped<StorageProviderAdministration>();
    builder.Services.AddScoped<StorageMoveService>();
    builder.Services.AddScoped<StorageMoveRunner>();
    builder.Services.AddHostedService<StorageMoveWorker>();
    builder.Services.AddScoped<StorageProviderService>();
    builder.Services.AddScoped<HelpDeskUiService>();
    builder.Services.AddScoped<CertificateService>();
    builder.Services.AddScoped<SiteService>();
    builder.Services.AddScoped<WindowsUpdateService>();
    builder.Services.AddScoped<EmailNotificationUiService>();
    builder.Services.AddScoped<ActionDeploymentUiService>();
    builder.Services.AddScoped<ServerSettingsUiService>();
    builder.Services.AddScoped<LoginOptionsQuery>();
    builder.Services.AddOptions<StorageMountOptions>().BindConfiguration("StorageMounts")
        .PostConfigure(options => options.RootPath = ServerDataPaths.MountsRelative)
        .Validate(x => !string.IsNullOrWhiteSpace(x.RootPath) && x.CheckIntervalSeconds >= 10, "Configure a storage mount root and a check interval of at least 10 seconds.").ValidateOnStart();
    builder.Services.AddSingleton<StorageMountPaths>();
    builder.Services.AddSingleton<StorageContentStorage>();
    builder.Services.AddSingleton<StorageMountCoordinator>();
    builder.Services.AddScoped<StorageMountService>();
    builder.Services.AddHostedService<StorageMountWorker>();
    builder.Services.AddScoped<AppUserAdministration>();
    builder.Services.AddScoped<FirstAdminSetup>();
    builder.Services.AddBrowserLoginProtection();
    builder.Services.AddScoped<AppUserService>();
    builder.Services.AddScoped<LocalLoginService>();
    builder.Services.AddScoped<AccountPasswordService>();
    builder.Services.AddScoped<IAdPasswordValidator, AdPasswordValidator>();
    builder.Services.AddScoped<AdLoginService>();
    builder.Services.AddScoped<ISecretProtector, SecretProtector>();
    builder.Services.AddScoped<CertificateAuthorityService>();
    builder.Services.AddScoped<WebCertificateService>();
    builder.Services.AddScoped<WebHttpsListenerService>();
    builder.Services.AddScoped<CertificateAuthorityAdministration>();
    builder.Services.AddScoped<IAuthorizationHandler, MfaEnrollmentHandler>();
    builder.Services.AddScoped<IAuthorizationHandler, PermissionHandler>();
    builder.Services.AddScoped<AppUserPermissionService>();
    builder.Services.AddScoped<AppUserGroupService>();
    builder.Services.AddScoped<SoftwareActionService>();
    builder.Services.AddScoped<ImagingService>();
    builder.Services.AddScoped<ImagingInspectionService>();
    builder.Services.AddScoped<ImageAdministration>();
    builder.Services.AddScoped<DeploymentProfileAdministration>();
    builder.Services.AddScoped<WinPeService>();
    builder.Services.AddScoped<WinPeAdministration>(sp => { var service = ActivatorUtilities.CreateInstance<WinPeAdministration>(sp); service.Windows = sp.GetRequiredService<WindowsWorkQueue>(); return service; });
    builder.Services.AddScoped<WinPeSetupService>(sp => { var service = ActivatorUtilities.CreateInstance<WinPeSetupService>(sp); service.Windows = sp.GetRequiredService<WindowsWorkQueue>(); return service; });
    builder.Services.AddSingleton<ReplicationSettingsStore>();
    builder.Services.AddSingleton<ReplicationTransfers>();
    builder.Services.AddScoped<ReplicationSettingsAdministration>();
    builder.Services.AddSingleton<SharedContentFiles>(sp => new(sp.GetRequiredService<StorageContentStorage>()) { BootRoot = sp.GetRequiredService<TftpRootDirectory>().RootPath, Transfers = sp.GetRequiredService<ReplicationTransfers>() });
    builder.Services.AddSingleton<SharedContentDelivery>();
    builder.Services.AddScoped<SharedContentInventory>();
    builder.Services.AddScoped<SharedReplicationRunner>();
    builder.Services.AddScoped<SiteStorageService>();
    builder.Services.AddSingleton<WindowsWorkQueue>();
    builder.Services.AddSingleton<WindowsProcessing>();
    builder.Services.AddSingleton<WindowsWinPeBridge>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<WindowsWinPeBridge>());
    builder.Services.AddScoped<SiteDownloadTickets>();
    builder.Services.AddScoped<SiteDownloadDelivery>(sp => new(sp.GetRequiredService<ICloneDeployDbFactory>(), sp.GetRequiredService<IHttpContextAccessor>())
        { Selection = sp.GetRequiredService<StorageReadSelection>() });
    builder.Services.AddScoped<SharedReplicationAdministration>();
    builder.Services.AddHostedService<SharedReplicationWorker>();
    builder.Services.AddSingleton<WinPeArtifactStorage>(sp => new(sp.GetRequiredService<CloneDeploy_Server.WinPe.BuildStorage>(), sp.GetRequiredService<StorageContentStorage>())
        { Shared = sp.GetRequiredService<SharedContentDelivery>() });
    builder.Services.AddSingleton(new CloneDeploy_Server.WinPe.ImagerBundle(
        Path.GetFullPath("Packaging/Imager/win-x64.zip", AppContext.BaseDirectory),
        null)
    {
        Arm64PackagePath = Path.GetFullPath("Packaging/Imager/win-arm64.zip", AppContext.BaseDirectory),
        TimeServers = builder.Configuration.GetSection("WinPe:TimeServers").Get<string[]>() ?? ["time.windows.com"]
    });
    builder.Services.AddHostedService<WinPeTransferWorker>();
    builder.Services.AddScoped<WinPeVariantActivation>();
    builder.Services.AddSingleton(new CloneDeploy_Server.WinPe.BuildStorage(new ServerDataPaths(builder.Environment).WinPe));
    builder.Services.AddScoped<DeploymentProfileService>();
    builder.Services.AddScoped<ImagePrepProfileService>();
    builder.Services.AddScoped<OuNamingRuleService>();
    builder.Services.AddScoped<OuNamingResolver>();
    builder.Services.AddScoped<ImagePrepCredentialService>();
    builder.Services.AddScoped<ImagingDeploymentService>();
    builder.Services.AddScoped<ImageVersionService>();
    builder.Services.AddScoped<DriverPackService>();
    builder.Services.AddScoped<AgentDriverPackImport>();
    builder.Services.AddScoped<DriverService>();
    builder.Services.AddScoped<DriverFileService>();
    builder.Services.AddScoped<DriverRefreshService>();
    builder.Services.AddOptions<DriverPreparationOptions>().BindConfiguration("DriverPreparation")
        .Validate(x => x.MaxExpandedBytes > 0 && x.MaxFiles > 0 && x.TimeoutMinutes > 0, "Driver preparation limits must be positive.").ValidateOnStart();
    builder.Services.AddSingleton<DriverPreparationStorage>();
    builder.Services.AddSingleton<DriverPackageExtractor>(sp => new(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<DriverPreparationOptions>>()) { RemoteExtract = sp.GetRequiredService<WindowsProcessing>().ExtractAsync });
    builder.Services.AddHostedService<DriverPreparationWorker>();
    builder.Services.AddSingleton<DriverCatalogClient>(sp => new(sp.GetRequiredService<IHttpClientFactory>(), builder.Environment) { RemoteCatalog = sp.GetRequiredService<WindowsProcessing>().CatalogAsync });
    builder.Services.AddHttpClient("DriverCatalogs", client =>
    {
        client.Timeout = TimeSpan.FromMinutes(2);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CloneDeploy-Server/1.0");
    });
    builder.Services.AddScoped<ActionLifecycleService>();
    builder.Services.AddScoped<ActionService>();
    builder.Services.AddScoped<PolicyService>();
    builder.Services.AddScoped<DeviceNotificationHistoryService>();
    builder.Services.AddScoped<DirectoryUserActivityService>();
    builder.Services.AddScoped<DeviceDirectoryRelations>();
    builder.Services.AddScoped<DeviceAuditService>();
    builder.Services.AddScoped<PolicyAdministration>();
    builder.Services.AddScoped<ActionDeploymentService>();
    builder.Services.AddScoped<DeviceTaskQueue>();
    builder.Services.AddHostedService<DeviceTaskRunner>();
    builder.Services.AddScoped<PolicyActivationService>();
    builder.Services.AddScoped<AgentPolicyHistoryService>();
    builder.Services.AddScoped<AgentService>();
    builder.Services.AddScoped<PolicyTargetService>();
    builder.Services.AddScoped<PolicyAudienceService>();
    builder.Services.AddScoped<UserAudienceService>();
    builder.Services.AddScoped<SelfServiceCategoryService>();
builder.Services.AddScoped<AgentImagingBootService>();
    builder.Services.AddScoped<CommandActionService>();
    builder.Services.AddScoped<ScriptActionService>();
    builder.Services.AddScoped<FileActionService>();
    builder.Services.AddScoped<NotificationActionService>();
    builder.Services.AddScoped<PrinterActionService>();
    builder.Services.AddScoped<DomainJoinActionService>();
    builder.Services.AddScoped<WindowsUpdateActionService>();
    builder.Services.AddScoped<WindowsUpdateApprovalService>();
    builder.Services.AddScoped<WindowsUpdateAutoApprovalService>();
    builder.Services.AddScoped<WindowsUpdateAutoApproval>();
    builder.Services.AddScoped<IScheduledTaskHandler, WindowsUpdateAutoApprovalTaskHandler>();

    builder.Services.AddLocalization();
    builder.Services.AddTransient<MudBlazor.MudLocalizer, ComponentLocalizer>();
    var app = builder.Build();
    
    var supportedCultures = CloneDeploy_Server.Localization.UiLanguages.Supported.ToArray();

    var localizationOptions = new RequestLocalizationOptions
    {
        DefaultRequestCulture = new RequestCulture("en"),
        SupportedCultures = supportedCultures,
        SupportedUICultures = supportedCultures
    };
    localizationOptions.RequestCultureProviders.Insert(0, new CloneDeploy_Server.Localization.UserRequestCultureProvider());

    // Reject cross-listener requests before redirects, authentication callbacks or static files.
    app.UseImagerSessions();
    app.UseTrustedSiteProxies();
    app.UseServerListeners();

// Configure the HTTP request pipeline.
    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error", createScopeForErrors: true);
        // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
        app.UseHsts();
    }

    // API clients (including WebSocket handshakes) need the actual failure status, not an HTML page.
    app.UseWhen(context => !context.Request.Path.StartsWithSegments("/api"),
        pages => pages.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));
    // Do not redirect credential-bearing agent requests received over plaintext HTTP.
    app.UseAgentApiTransport();
    app.UseRouting();
    app.UseHttpsConnectionDiagnostics();
    app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15), KeepAliveTimeout = TimeSpan.FromSeconds(15) });
    app.UseBootAwareHttpsRedirection();

    app.UseAuthentication();
    app.UseRequestLocalization(localizationOptions);
    app.UseAgentResponseSigning();
    app.UseRateLimiter();
    app.UseMiddleware<ServerFeatureApiMiddleware>();
    app.UseImagerSessionAdmission();
    app.UseAuthorization();
    app.UseAntiforgery();

    app.MapStaticAssets()
        .AllowAnonymous();
    app.MapLocalAuthEndpoints();
    app.MapEntraAuthentication();
    app.MapControllers();
    app.MapAgentHub();
    app.MapRazorComponents<App>()
        .AddInteractiveServerRenderMode()
        .Add(endpoint =>
        {
            foreach (var connection in endpoint.Metadata.OfType<Microsoft.AspNetCore.Http.Connections.HttpConnectionDispatcherOptions>())
                connection.CloseOnAuthenticationExpiration = true;
            // Public pages need the Blazor transport. Routed pages keep their own authorization.
            if (!endpoint.Metadata.OfType<ComponentTypeMetadata>().Any())
                endpoint.Metadata.Add(new AllowAnonymousAttribute());
        });


    app.Run();
}
catch (HostAbortedException)
{
    // EF design-time commands abort the host after collecting services.
}
catch (Exception ex)
{
    StartupDiagnostics.WriteFailure(ex, startupDataDirectory);
    Log.Fatal(ex, "Application terminated unexpectedly");
    Environment.ExitCode = 1;
}
finally
{
    Log.CloseAndFlush();
}
