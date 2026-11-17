using System.Security.Cryptography.X509Certificates;

namespace CloneDeploy.SiteServices;

public interface ISiteProxyTrust
{
    // null means unmanaged; false explicitly denies a known registration (including config pins).
    ValueTask<bool?> IsTrustedAsync(X509Certificate2 certificate, CancellationToken ct);
}

public interface ISiteProxyAccessPolicy
{
    ValueTask<bool> AllowsAsync(X509Certificate2 certificate, Microsoft.AspNetCore.Http.PathString path, System.Net.IPAddress address, CancellationToken ct);
}
