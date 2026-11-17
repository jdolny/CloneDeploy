using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Localization;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Lets administrators manage and download the certificate used to establish trust with agents.
/// </summary>
public sealed class CertificateAuthorityService(CertificateAuthorityAdministration administration,
    ILogger<CertificateAuthorityService> logger)
{
    public Task<ServiceResult<CertificateAuthorityDetailsDto?>> GetAsync(ClaimsPrincipal user, CancellationToken ct = default) =>
        ExecuteAsync(() => administration.GetAsync(user, ct));

    public Task<ServiceResult<CertificateAuthorityDetailsDto>> GenerateAsync(ClaimsPrincipal user, string organization, CancellationToken ct = default) =>
        ExecuteAsync(() => administration.GenerateAsync(user, organization, ct));

    public Task<ServiceResult<byte[]?>> GetPublicCertificateAsync(ClaimsPrincipal user, CancellationToken ct = default) =>
        ExecuteAsync(() => administration.GetPublicCertificateAsync(user, ct));

    public static ServiceResult<X509Certificate2> Generate(string organization, DateTimeOffset now) =>
        RootCertificateGenerator.Generate(organization, now);

    private Task<ServiceResult<T>> ExecuteAsync<T>(Func<Task<ServiceResult<T>>> operation,
        [CallerMemberName] string operationName = "") =>
        ServiceOperations.RunAsync(operation, logger, ErrorKeys.CertificateAuthority.Failed, operationName: operationName);

}
