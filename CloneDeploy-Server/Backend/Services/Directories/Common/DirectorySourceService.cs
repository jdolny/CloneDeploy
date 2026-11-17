using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Localization;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using CloneDeploy_Server.Backend.Services.DynamicGroups;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Manages directory connection details, saved credentials, and synchronization schedules.
/// </summary>
public sealed class DirectorySourceService(
    ICloneDeployDbFactory dbFactory,
    AuthenticationStateProvider authenticationStateProvider,
    IAuthorizationService authorization)
{
    private async Task<bool> IsAdminAsync()
    {
        var user = (await authenticationStateProvider.GetAuthenticationStateAsync()).User;
        return (await authorization.AuthorizeAsync(user, Constants.Permissions.AdminOnly)).Succeeded;
    }

    public async Task<ServiceResult<List<DirectorySource>>> GetSourcesAsync(CancellationToken ct = default)
    {
        if (!await IsAdminAsync())
            return ServiceResult<List<DirectorySource>>.Fail(ErrorKeys.Directory.Forbidden);

        await using var db = await dbFactory.CreateContextAsync();
        return ServiceResult<List<DirectorySource>>.Ok(await db.DirectorySources.AsNoTracking().Include(x => x.LdapSettings).OrderBy(x => x.Name).ToListAsync(ct));
    }

    // Creation reserves the directory slot without enabling an incomplete connection.
    public async Task<ServiceResult<int>> CreateAsync(DirectorySource input, CancellationToken ct = default)
    {
        if (!await IsAdminAsync()) return ServiceResult<int>.Fail(ErrorKeys.Directory.Forbidden);
        if (input.Provider is not (DirectoryProvider.ActiveDirectory or DirectoryProvider.EntraId) ||
            string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > 200 || input.Description.Length > 2000)
            return ServiceResult<int>.Fail(ErrorKeys.Directory.Validation, UiText.Get(LocKeys.Ui.DirectorySourceService.EnterANameOfAtMost200CharactersAnd));
        await using var db = await dbFactory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await EntraAdministration.LockAsync(db, ct);
        if (await db.DirectorySources.AnyAsync(x => x.Provider == input.Provider || (!input.ApplicationAccessOnly && !x.ApplicationAccessOnly), ct))
            return ServiceResult<int>.Fail(ErrorKeys.Directory.Validation, UiText.Get(LocKeys.Ui.DirectorySourceService.OnlyOneConnectionPerProviderAndOneFullDirectory));
        var source = new DirectorySource { Name = input.Name.Trim(), Description = input.Description.Trim(),
            Provider = input.Provider, ApplicationAccessOnly = input.ApplicationAccessOnly, IsEnabled = false };
        db.DirectorySources.Add(source);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return ServiceResult<int>.Ok(source.Id);
    }

    public async Task<ServiceResult<DirectorySource>> NewLdapSourceAsync(CancellationToken ct = default)
    {
        if (!await IsAdminAsync())
            return ServiceResult<DirectorySource>.Fail(ErrorKeys.Directory.Forbidden);

        await using var db = await dbFactory.CreateContextAsync();
        var defaults = new ActiveDirectorySettings();
        return ServiceResult<DirectorySource>.Ok(new DirectorySource
        {
            Name = "Active Directory",
            Provider = DirectoryProvider.ActiveDirectory,
            LdapSettings = new LdapDirectorySettings
            {
                Host = defaults.Host,
                Port = defaults.Port,
                BaseDns = defaults.SearchBases(),
                ManagedCredentialId = defaults.ManagedCredentialId,
                PageSize = defaults.PageSize,
                TimeoutSeconds = defaults.TimeoutSeconds,
                MaxObjects = defaults.MaxObjects
            }
        });
    }

    public async Task<ServiceResult<List<DirectorySettingChoiceDto>>> GetCredentialsAsync(CancellationToken ct = default)
    {
        if (!await IsAdminAsync())
            return ServiceResult<List<DirectorySettingChoiceDto>>.Fail(ErrorKeys.Directory.Forbidden);

        await using var db = await dbFactory.CreateContextAsync();
        return ServiceResult<List<DirectorySettingChoiceDto>>.Ok(await db.ManagedCredentials.AsNoTracking().OrderBy(x => x.Name)
            .Select(x => new DirectorySettingChoiceDto(x.Id, x.Name + " (" + x.Username + ")")).ToListAsync(ct));
    }

    public async Task<ServiceResult<List<DirectorySettingChoiceDto>>> GetSchedulesAsync(CancellationToken ct = default)
    {
        if (!await IsAdminAsync())
            return ServiceResult<List<DirectorySettingChoiceDto>>.Fail(ErrorKeys.Directory.Forbidden);

        await using var db = await dbFactory.CreateContextAsync();
        return ServiceResult<List<DirectorySettingChoiceDto>>.Ok(await db.Schedules.AsNoTracking().OrderBy(x => x.Name)
            .Select(x => new DirectorySettingChoiceDto(x.Id, x.Name)).ToListAsync(ct));
    }

    public async Task<ServiceResult<int>> SaveLdapSourceAsync(DirectorySource input, CancellationToken ct = default)
    {
        if (!await IsAdminAsync())
            return ServiceResult<int>.Fail(ErrorKeys.Directory.Forbidden);

        if (input.Provider != DirectoryProvider.ActiveDirectory || input.LdapSettings is null)
            return ServiceResult<int>.Fail(ErrorKeys.Directory.Validation, UiText.Get(LocKeys.Ui.DirectorySourceService.SelectAnActiveDirectoryLDAPConnection));

        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > 200)
            return ServiceResult<int>.Fail(ErrorKeys.Directory.Validation, UiText.Get(LocKeys.Ui.DirectorySourceService.EnterAConnectionNameOfAtMost200Characters));

        if (input.Description.Length > 2000)
            return ServiceResult<int>.Fail(ErrorKeys.Directory.Validation, UiText.Get(LocKeys.Ui.DirectorySourceService.DescriptionMustBeAtMost2000Characters));

        var options = ToOptions(input);
        var validationError = options.GetValidationError();
        if (validationError is not null)
            return ServiceResult<int>.Fail(ErrorKeys.Directory.Validation, validationError);

        if (Uri.CheckHostName(options.Host) == UriHostNameType.Unknown)
            return ServiceResult<int>.Fail(ErrorKeys.Directory.Validation, UiText.Get(LocKeys.Ui.DirectorySourceService.EnterTheDomainControllerHostnameWithoutAURLOr));

        await using var db = await dbFactory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await EntraAdministration.LockAsync(db, ct);
        if (await db.DirectorySources.AnyAsync(x => x.Id != input.Id && (x.Provider == input.Provider || (!input.ApplicationAccessOnly && !x.ApplicationAccessOnly)), ct))
            return ServiceResult<int>.Fail(ErrorKeys.Directory.Validation, UiText.Get(LocKeys.Ui.DirectorySourceService.OnlyOneConnectionPerProviderAndOneFullDirectory));
        if (!await db.ManagedCredentials.AnyAsync(x => x.Id == options.ManagedCredentialId, ct))
            return ServiceResult<int>.Fail(ErrorKeys.Directory.Validation, UiText.Get(LocKeys.Ui.DirectorySourceService.SelectAnExistingManagedCredential));

        if (input.ScheduleId is { } scheduleId && !await db.Schedules.AnyAsync(x => x.Id == scheduleId, ct))
            return ServiceResult<int>.Fail(ErrorKeys.Directory.Validation, UiText.Get(LocKeys.Ui.DirectorySourceService.SelectAnExistingSchedule));

        var stored = input.Id == 0
            ? new DirectorySource { Provider = input.Provider }
            : await db.DirectorySources.Include(x => x.LdapSettings).SingleOrDefaultAsync(x => x.Id == input.Id, ct);
        if (stored is null)
            return ServiceResult<int>.Fail(ErrorKeys.Directory.Validation, UiText.Get(LocKeys.Ui.DirectorySourceService.ThisDirectorySourceNoLongerExists));

        if (stored.Provider != input.Provider)
            return ServiceResult<int>.Fail(ErrorKeys.Directory.Validation, UiText.Get(LocKeys.Ui.DirectorySourceService.ASavedSourceSProviderCannotChangeCreateAnother));

        if (!stored.ApplicationAccessOnly && input.ApplicationAccessOnly)
            await InvalidateDeviceGroupsAsync(db, stored.Id, ct);
        stored.ApplicationAccessOnly = input.ApplicationAccessOnly;
        stored.Description = input.Description.Trim();
        stored.Name = input.Name.Trim();
        stored.IsEnabled = input.IsEnabled;
        stored.ScheduleId = input.ScheduleId;
        stored.LdapSettings ??= new LdapDirectorySettings();
        stored.LdapSettings.Host = options.Host;
        stored.LdapSettings.Port = options.Port;
        stored.LdapSettings.BaseDns = options.SearchBases();
        stored.LdapSettings.ManagedCredentialId = options.ManagedCredentialId;
        stored.LdapSettings.PageSize = options.PageSize;
        stored.LdapSettings.TimeoutSeconds = options.TimeoutSeconds;
        stored.LdapSettings.MaxObjects = options.MaxObjects;
        if (input.Id == 0) db.DirectorySources.Add(stored);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return ServiceResult<int>.Ok(stored.Id);
    }

    public async Task<ServiceResult<int>> SaveEntraSourceAsync(DirectorySource input, CancellationToken ct = default)
    {
        if (!await IsAdminAsync()) return ServiceResult<int>.Fail(ErrorKeys.Directory.Forbidden);
        if (input.Provider != DirectoryProvider.EntraId || string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > 200 || input.Description.Length > 2000)
            return ServiceResult<int>.Fail(ErrorKeys.Directory.Validation, UiText.Get(LocKeys.Ui.DirectorySourceService.EnterAValidEntraConnectionName));
        await using var db = await dbFactory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await EntraAdministration.LockAsync(db, ct);
        if (await db.DirectorySources.AnyAsync(x => x.Id != input.Id && (x.Provider == input.Provider || (!input.ApplicationAccessOnly && !x.ApplicationAccessOnly)), ct))
            return ServiceResult<int>.Fail(ErrorKeys.Directory.Validation, UiText.Get(LocKeys.Ui.DirectorySourceService.OnlyOneConnectionPerProviderAndOneFullDirectory));
        if (!await db.Set<EntraConnection>().AnyAsync(x => x.TenantId != Guid.Empty && x.ClientId != Guid.Empty &&
                x.CertificateEncrypted != null && x.CertificateEncrypted != "", ct))
            return ServiceResult<int>.Fail(ErrorKeys.Directory.Validation, UiText.Get(LocKeys.Ui.DirectorySourceService.ConfigureAndActivateTheEntraApplicationCertificateFirst));
        var stored = input.Id == 0 ? new DirectorySource { Provider = DirectoryProvider.EntraId }
            : await db.DirectorySources.SingleOrDefaultAsync(x => x.Id == input.Id, ct);
        if (stored is null || stored.Provider != DirectoryProvider.EntraId)
            return ServiceResult<int>.Fail(ErrorKeys.Directory.Validation, UiText.Get(LocKeys.Ui.DirectorySourceService.ThisEntraDirectoryNoLongerExists));
        if (!stored.ApplicationAccessOnly && input.ApplicationAccessOnly)
            await InvalidateDeviceGroupsAsync(db, stored.Id, ct);
        stored.ApplicationAccessOnly = input.ApplicationAccessOnly;
        stored.Description = input.Description.Trim();
        stored.Name = input.Name.Trim(); stored.IsEnabled = input.IsEnabled;
        if (input.Id == 0) db.DirectorySources.Add(stored);
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        return ServiceResult<int>.Ok(stored.Id);
    }

    public async Task<ServiceResult> DeleteAsync(int sourceId, CancellationToken ct = default)
    {
        if (!await IsAdminAsync())
            return ServiceResult.Fail(ErrorKeys.Directory.Forbidden);

        await using var db = await dbFactory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await EntraAdministration.LockAsync(db, ct);
        var lockName = "ad-sync:" + sourceId;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockName}, 0))", ct);

        // Foreign keys remove directory data and clear both directory references on linked devices.
        if (await db.OuNamingRuleSets.AnyAsync(x => x.DirectorySourceId == sourceId, ct))
            return ServiceResult.Fail(ErrorKeys.Directory.Validation, UiText.Get(LocKeys.Ui.DirectorySourceService.RemoveOUNamingRuleSetsThatUseThisDirectory));
        // Devices and their inventory, history, and lifecycle state are retained.
        await db.DirectorySources.Where(x => x.Id == sourceId).ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
        return ServiceResult.Ok();
    }

    private static async Task InvalidateDeviceGroupsAsync(CloneDeployDbContext db, int sourceId, CancellationToken ct)
    {
        if (sourceId == 0) return;
        var groups = await db.DeviceGroups.Where(x => x.Type == DeviceGroupType.Dynamic && x.QueryDefinitionJson != null)
            .OrderBy(x => x.Id).ToListAsync(ct);
        foreach (var group in groups)
        {
            bool affected;
            try { affected = UsesDirectory(DeviceQueryDefinitionDto.Parse(group.QueryDefinitionJson!).Root); }
            catch (InvalidOperationException) { affected = true; }
            if (!affected) continue;
            // Serialize against rebuilds; the row update also invalidates an older repeatable-read rebuild.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"device-group:" + group.Id}, 0))", ct);
            await db.DeviceGroupMemberships.Where(x => x.DeviceGroupId == group.Id).ExecuteDeleteAsync(ct);
            await db.DeviceGroups.Where(x => x.Id == group.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.MembershipEvaluatedAtUtc, (DateTime?)null), ct);
        }

        bool UsesDirectory(DeviceQueryNodeDto node) =>
            (node.Scope is "DirectoryGroup" or "DirectoryOu" && node.DirectorySourceId == sourceId) ||
            node.Field is "IsAdSync" or "AdDisabled" || node.Children.Any(UsesDirectory);
    }

    internal static ActiveDirectorySettings ToOptions(DirectorySource source)
    {
        var ldap = source.LdapSettings ?? throw new InvalidOperationException("LDAP settings are missing.");
        return new ActiveDirectorySettings
        {
            DirectorySourceId = source.Id,
            Host = ldap.Host.Trim(),
            Port = ldap.Port,
            BaseDns = ldap.BaseDns.ToArray(),
            ManagedCredentialId = ldap.ManagedCredentialId,
            PageSize = ldap.PageSize,
            TimeoutSeconds = ldap.TimeoutSeconds,
            MaxObjects = ldap.MaxObjects
        };
    }
}
