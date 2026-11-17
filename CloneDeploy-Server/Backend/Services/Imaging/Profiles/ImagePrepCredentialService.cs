using System.Security.Cryptography;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

// The captured image carries an authenticated capability for only its configured credentials.
// Passwords are read at deployment time, never embedded in this ticket or the stored profile.
public sealed class ImagePrepCredentialService(IDataProtectionProvider protection)
{
    private readonly IDataProtector protector = protection.CreateProtector("CloneDeploy.ImagePrep.Credentials.v1");
    public sealed record Binding(string Marker, int Id, Guid CredentialGuid, string Username);
    public sealed record Ticket(int ProfileId, Guid MarkerId, Binding[] Bindings);
    public async Task<string?> CreateAsync(CloneDeployDbContext db, ImagePrepProfile profile, CancellationToken ct)
    {
        var options = profile.GeneratorOptions;
        if (options is null || !options.CredentialIds().Any()) return null;
        var ids = options.CredentialIds().ToArray();
        var credentials = await db.ManagedCredentials.AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync(ct);
        var validation = ImagePrepAnswerGenerator.Validate(options, credentials.ToDictionary(x => x.Id, x => x.Username));
        if (validation is not null) throw new InvalidOperationException(validation);
        if (profile.GenerateAnswerFile) profile.AnswerFile = ImagePrepAnswerGenerator.Generate(options, credentials.ToDictionary(x => x.Id, x => x.Username));
        var bindings = credentials.Where(x => profile.AnswerFile?.Contains(options.Marker(x.Id), StringComparison.Ordinal) == true)
            .Select(x => new Binding(options.Marker(x.Id), x.Id, x.Guid, x.Username)).ToArray();
        return bindings.Length == 0 ? null : protector.Protect(JsonSerializer.Serialize(new Ticket(profile.Id, options.MarkerId, bindings)));
    }

    public async Task<Dictionary<string, string>> ResolveAsync(CloneDeployDbContext db, string ticket, ISecretProtector secrets, CancellationToken ct)
    {
        Ticket authorization;
        try { authorization = JsonSerializer.Deserialize<Ticket>(protector.Unprotect(ticket)) ?? throw new CryptographicException(); }
        catch (Exception ex) when (ex is CryptographicException or JsonException or ArgumentException) { throw new UnauthorizedAccessException(); }
        var profile = await db.ImagePrepProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == authorization.ProfileId && x.Enabled, ct);
        var options = profile?.GeneratorOptions;
        if (options is null || options.MarkerId != authorization.MarkerId)
            throw new InvalidOperationException("The image prep credential configuration was removed or disabled. Prepare and capture the image again.");
        var permitted = options.CredentialIds().ToHashSet();
        var result = new Dictionary<string, string>();
        foreach (var binding in authorization.Bindings)
        {
            if (!permitted.Contains(binding.Id) || binding.Marker != options.Marker(binding.Id)) throw new UnauthorizedAccessException();
            var credential = await db.ManagedCredentials.AsNoTracking().SingleOrDefaultAsync(x => x.Id == binding.Id && x.Guid == binding.CredentialGuid, ct);
            if (credential is null || credential.Username != binding.Username)
                throw new InvalidOperationException("A Sysprep managed credential was removed or its username changed. Prepare and capture the image again.");
            result.Add(binding.Marker, secrets.Unprotect(credential.PasswordEncrypted));
        }
        return result;
    }
}
