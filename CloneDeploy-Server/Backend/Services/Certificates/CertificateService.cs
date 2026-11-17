using System.Text.Json.Nodes;
using System.Security.Cryptography.X509Certificates;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using CloneDeploy_Server.Localization;
using CloneDeploy_Server.Backend.Enum;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy.SiteServices;
using System.Runtime.CompilerServices;
using CloneDeploy_Server.Backend.Common;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>Web UI entry point for Certificates operations.</summary>
public sealed class CertificateService(
    CertificateAuthorityService authority,
    WebCertificateService web,
    WebHttpsListenerService listener,
    ILogger<CertificateService> logger)
{
    public Task<ServiceResult<CertificateAuthorityDetailsDto?>> GetAuthorityAsync(ClaimsPrincipal user, CancellationToken ct = default) =>
        ExecuteAsync(() => authority.GetAsync(user, ct));

    public Task<ServiceResult<CertificateAuthorityDetailsDto>> AuthorityGenerateAsync(ClaimsPrincipal user, string organization, CancellationToken ct = default) =>
        ExecuteAsync(() => authority.GenerateAsync(user, organization, ct));

    public Task<ServiceResult<WebCertificateDetails?>> GetWebCertificateAsync(ClaimsPrincipal user) =>
        ExecuteAsync(() => web.GetAsync(user));

    public Task<ServiceResult> SaveWebCertificateAsync(ClaimsPrincipal user, string addresses, bool enabled) =>
        ExecuteAsync(() => web.SaveAsync(user, addresses, enabled));

    public Task<ServiceResult<WebHttpsListenerDetails>> GetListenerAsync(ClaimsPrincipal user) =>
        ExecuteAsync(() => listener.GetAsync(user));

    public Task<ServiceResult> SaveListenerPortsAsync(ClaimsPrincipal user, bool enabled, int managementPort, int servicePort, int portalPort) =>
        ExecuteAsync(() => listener.SavePortsAsync(user, enabled, managementPort, servicePort, portalPort));

    public Task<ServiceResult> SaveServerHttpsAsync(ClaimsPrincipal user, int managementPort, int servicePort, int portalPort, bool useManagedHttps) =>
        ExecuteAsync(() => listener.SaveServerHttpsAsync(user, managementPort, servicePort, portalPort, useManagedHttps));

    private Task<ServiceResult> ExecuteAsync(Func<Task<ServiceResult>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, operationName: operationName);

    private Task<ServiceResult<T>> ExecuteAsync<T>(Func<Task<ServiceResult<T>>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, operationName: operationName);

    public static string? PublicHost(string? url) =>
        WebCertificateAddresses.PublicHost(url);

    public static string[] SuggestedHosts(string? publicUrl, IEnumerable<string> existing) =>
        WebCertificateAddresses.SuggestedHosts(publicUrl, existing);
}
