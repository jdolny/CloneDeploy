using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using CloneDeploy.SiteServices;
using CloneDeploy_Server.Backend.Common;
using Microsoft.AspNetCore.Authorization;

namespace CloneDeploy_Server.Backend.Services;

public sealed record WebHttpsListenerDetails(bool Available, bool Enabled, int Port)
{ public bool UseManagedHttps { get; init; } = true; public int ServicePort { get; init; } = 8443; public int PortalPort { get; init; } = 8444; }

public sealed record AgentToolsAccessDetails(bool Available, string AllowedNetworks, string ActiveNetworks);

public sealed class WebHttpsListenerService(
    IWebHostEnvironment environment,
    IConfiguration configuration,
    IAuthorizationService authorization,
    WebCertificateService certificates,
    ILogger<WebHttpsListenerService> logger,
    ServerRuntimePaths? runtimePaths)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private string ConfigurationPath => runtimePaths?.ConfigurationFile ?? Path.Combine(environment.ContentRootPath, "server.json");
    private const string AgentToolsNetworksKey = "Server:AgentAdmin:AllowedNetworks";

    public Task<ServiceResult<AgentToolsAccessDetails>> GetAgentToolsAccessAsync(ClaimsPrincipal user) => ServiceOperations.RunAsync(async () =>
    {
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.AdminOnly)).Succeeded)
            return ServiceResult<AgentToolsAccessDetails>.Fail("AgentToolsAccess.Forbidden");
        var active = configuration[AgentToolsNetworksKey] ?? "";
        if (!File.Exists(ConfigurationPath))
            return ServiceResult<AgentToolsAccessDetails>.Ok(new(false, active, active));
        using var saved = new ConfigurationManager();
        saved.AddJsonFile(ConfigurationPath);
        return ServiceResult<AgentToolsAccessDetails>.Ok(new(true, saved[AgentToolsNetworksKey] ?? active, active));
    }, logger, "AgentToolsAccess.Failed");

    public Task<ServiceResult> SaveAgentToolsAccessAsync(ClaimsPrincipal user, string? allowedNetworks) => ServiceOperations.RunAsync(async () =>
    {
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.AdminOnly)).Succeeded)
            return ServiceResult.Fail("AgentToolsAccess.Forbidden");
        var networks = allowedNetworks?.Trim() ?? "";
        try { ClientNetworks.Validate(networks); }
        catch (InvalidDataException ex) { return ServiceResult.Fail("AgentToolsAccess.Validation", ex.Message); }
        // Share the listener editor's lock: neither editor may overwrite the other's settings.
        await Gate.WaitAsync();
        try
        {
            if (!File.Exists(ConfigurationPath))
                return ServiceResult.Fail("AgentToolsAccess.Validation", UiText.Get(LocKeys.Ui.WebHttpsListenerService.AgentToolsNetworkManagementRequiresServerJsonFromThe));
            var document = JsonNode.Parse(await File.ReadAllTextAsync(ConfigurationPath))!.AsObject();
            var server = document["Server"] as JsonObject;
            if (server is null) { server = new JsonObject(); document["Server"] = server; }
            var admin = server["AgentAdmin"] as JsonObject;
            if (admin is null) { admin = new JsonObject(); server["AgentAdmin"] = admin; }
            admin["AllowedNetworks"] = networks;
            var temporary = ConfigurationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporary, ConfigurationPath, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            logger.LogInformation("Administrator {User} configured agent tools allowed networks: {Networks}. Restart required.", user.Identity?.Name, networks);
            return ServiceResult.Ok();
        }
        finally { Gate.Release(); }
    }, logger, "AgentToolsAccess.Failed");
    public Task<ServiceResult<WebHttpsListenerDetails>> GetAsync(ClaimsPrincipal user) => ServiceOperations.RunAsync(async () =>
    {
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.AdminOnly)).Succeeded)
            return ServiceResult<WebHttpsListenerDetails>.Fail("CertificateAuthority.Forbidden");
        var config = File.Exists(ConfigurationPath) ? new ConfigurationBuilder().AddConfiguration(configuration).AddJsonFile(ConfigurationPath).Build() : configuration;
        var ports = ListenerPorts.Read(config);
        return ServiceResult<WebHttpsListenerDetails>.Ok(new(File.Exists(ConfigurationPath), ports.Enabled, ports.ManagementPort)
            { ServicePort = ports.ServicePort, PortalPort = ports.PortalPort, UseManagedHttps = config.GetValue("Certificates:UseManagedHttps", true) });
    }, logger, "CertificateAuthority.Failed");

    public Task<ServiceResult> SaveAsync(ClaimsPrincipal user, bool enabled, int port) =>
        SavePortsAsync(user, enabled, port, ListenerPorts.Read(configuration).ServicePort, ListenerPorts.Read(configuration).PortalPort);

    public Task<ServiceResult> SavePortsAsync(ClaimsPrincipal user, bool enabled, int managementPort, int servicePort, int portalPort) =>
        SaveCoreAsync(user, enabled, managementPort, servicePort, portalPort, null);

    public Task<ServiceResult> SaveServerHttpsAsync(ClaimsPrincipal user, int managementPort, int servicePort, int portalPort, bool useManagedHttps) =>
        SaveCoreAsync(user, true, managementPort, servicePort, portalPort, useManagedHttps);

    private Task<ServiceResult> SaveCoreAsync(ClaimsPrincipal user, bool enabled, int managementPort, int servicePort, int portalPort, bool? useManagedHttps) => ServiceOperations.RunAsync(async () =>
    {
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.AdminOnly)).Succeeded)
            return ServiceResult.Fail("CertificateAuthority.Forbidden");
        if (enabled)
        {
            var certificate = await certificates.GetAsync(user);
            if (!certificate.Success || certificate.Data is not { Enabled: true })
                return ServiceResult.Fail("CertificateAuthority.Validation", UiText.Get(LocKeys.Ui.WebHttpsListenerService.IssueAndEnableAManagedHTTPSCertificateFirst));
        }
        await Gate.WaitAsync();
        try
        {
            if (!File.Exists(ConfigurationPath)) return ServiceResult.Fail("CertificateAuthority.Validation", UiText.Get(LocKeys.Ui.WebHttpsListenerService.ListenerManagementRequiresServerJsonFromTheServerInstaller));
            var config = new ConfigurationBuilder().AddConfiguration(configuration).AddJsonFile(ConfigurationPath).Build();
            var ports = ListenerPorts.Read(config);
            ports.Enabled = enabled; ports.ManagementPort = managementPort; ports.ServicePort = servicePort; ports.PortalPort = portalPort;
            try { ports.Validate(); }
            catch (InvalidDataException ex) { return ServiceResult.Fail("CertificateAuthority.Validation", ex.Message); }
            if (enabled && !(useManagedHttps ?? config.GetValue("Certificates:UseManagedHttps", true)))
            {
                try { ValidateExternalCertificate(config.GetSection("Kestrel:Certificates:Default")); }
                catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
                { return ServiceResult.Fail("CertificateAuthority.Validation", UiText.Get("Certificates.ExternalHttpsRequired")); }
            }
            var document = JsonNode.Parse(await File.ReadAllTextAsync(ConfigurationPath))!.AsObject();
            var server = document["Server"] as JsonObject;
            if (server is null) { server = new JsonObject(); document["Server"] = server; }
            server["Listeners"] = JsonSerializer.SerializeToNode(ports);
            if (useManagedHttps is { } managed)
            {
                var settings = document["Certificates"] as JsonObject;
                if (settings is null) { settings = new JsonObject(); document["Certificates"] = settings; }
                settings["UseManagedHttps"] = managed;
            }
            var temporary = ConfigurationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporary, ConfigurationPath, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            logger.LogInformation("Administrator {User} configured listeners: enabled={Enabled}, management={Management}, service={Service}, portal={Portal}. Restart required.", user.Identity?.Name, enabled, managementPort, servicePort, portalPort);
            return ServiceResult.Ok();
        }
        finally { Gate.Release(); }
    }, logger, "CertificateAuthority.Failed");

    private void ValidateExternalCertificate(IConfigurationSection settings)
    {
        X509Certificate2 certificate;
        if (settings["Path"] is { Length: > 0 } path)
        {
            path = Path.GetFullPath(path, environment.ContentRootPath);
            if (settings["KeyPath"] is { Length: > 0 } keyPath)
            {
                keyPath = Path.GetFullPath(keyPath, environment.ContentRootPath);
                certificate = settings["Password"] is { Length: > 0 } password
                    ? X509Certificate2.CreateFromEncryptedPemFile(path, password, keyPath)
                    : X509Certificate2.CreateFromPemFile(path, keyPath);
            }
            else certificate = X509CertificateLoader.LoadPkcs12FromFile(path, settings["Password"], X509KeyStorageFlags.EphemeralKeySet);
        }
        else if (settings["Subject"] is { Length: > 0 } subject)
            certificate = Microsoft.AspNetCore.Server.Kestrel.Https.CertificateLoader.LoadFromStoreCert(subject,
                settings["Store"] ?? "My", System.Enum.Parse<StoreLocation>(settings["Location"] ?? "CurrentUser", true), settings.GetValue("AllowInvalid", false));
        else throw new InvalidOperationException("External HTTPS certificate is not configured.");
        using (certificate)
        {
            var now = DateTime.UtcNow;
            var usages = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().ToList();
            if (!certificate.HasPrivateKey || certificate.NotBefore.ToUniversalTime() > now || certificate.NotAfter.ToUniversalTime() <= now ||
                usages.Any(x => !x.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value is "1.3.6.1.5.5.7.3.1" or "2.5.29.37.0")))
                throw new InvalidOperationException("External HTTPS certificate is not usable for server authentication.");
        }
    }
}
