using System.Security.Claims;
using System.Text.Json;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed record InstallationWelcomeRecord(int AdministratorId, string? AdministratorName,
    DateTimeOffset ShownAtUtc, string NoticesVersion, DateTimeOffset? ContinuedAtUtc = null);

public sealed class InstallationWelcome(ICloneDeployDbFactory factory,
    AuthenticationStateProvider authentication, IAuthorizationService authorization)
{
    public const string SettingKey = "Installation.Welcome";
    public const string NoticesVersion = "1";

    public async Task<bool> TryShowAsync()
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        if (!int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) ||
            !(await authorization.AuthorizeAsync(user, Constants.Permissions.AdminOnly)).Succeeded) return false;
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        // Only one administrator/circuit may claim the installation-wide introduction.
        await db.Database.ExecuteSqlRawAsync("LOCK TABLE \"AppSettings\" IN SHARE ROW EXCLUSIVE MODE");
        if (await db.AppSettings.AnyAsync(x => x.Name == SettingKey)) return false;
        db.AppSettings.Add(new AppSetting
        {
            Name = SettingKey,
            Value = JsonSerializer.Serialize(new InstallationWelcomeRecord(userId, user.Identity?.Name,
                DateTimeOffset.UtcNow, NoticesVersion))
        });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return true;
    }

    public async Task ContinueAsync()
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        if (!int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) ||
            !(await authorization.AuthorizeAsync(user, Constants.Permissions.AdminOnly)).Succeeded)
            throw new UnauthorizedAccessException();
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("LOCK TABLE \"AppSettings\" IN SHARE ROW EXCLUSIVE MODE");
        var row = await db.AppSettings.SingleAsync(x => x.Name == SettingKey);
        var record = JsonSerializer.Deserialize<InstallationWelcomeRecord>(row.Value!)!;
        if (record.AdministratorId != userId || record.ContinuedAtUtc is not null) return;
        row.Value = JsonSerializer.Serialize(record with { ContinuedAtUtc = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }
}

public static class OpenSourceNotices
{
    public const string SourceUrl = "https://github.com/jdolny/CloneDeploy";
    public static IReadOnlyList<(string Name, string Text)> Documents { get; } = Load();

    private static IReadOnlyList<(string Name, string Text)> Load()
    {
        var assembly = typeof(OpenSourceNotices).Assembly;
        return assembly.GetManifestResourceNames().Where(x => x.StartsWith("CloneDeploy.Legal.", StringComparison.Ordinal))
            .OrderBy(x => x, StringComparer.Ordinal).Select(name =>
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(stream);
                return (name["CloneDeploy.Legal.".Length..], reader.ReadToEnd());
            }).ToArray();
    }
}
