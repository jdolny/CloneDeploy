using Microsoft.AspNetCore.DataProtection;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Protects saved secrets and reads them back when the application needs them.
/// </summary>
public sealed class SecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    // Persisted credentials and certificate passwords depend on this purpose.
    // Changing it requires a migration using the original protection keys.
    private readonly IDataProtector protector =
        provider.CreateProtector("CloneDeploy.Secrets");

    public string Protect(string value) =>
        protector.Protect(value);

    public string Unprotect(string value) =>
        protector.Unprotect(value);
}