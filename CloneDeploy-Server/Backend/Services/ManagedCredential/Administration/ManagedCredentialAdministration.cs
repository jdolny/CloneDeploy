using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Stores protected credentials and checks whether they can be changed or deleted.
/// </summary>
public sealed class ManagedCredentialAdministration(
    ICloneDeployDbFactory dbFactory,
    ISecretProtector secretProtector,
    AuthenticationStateProvider authentication,
    IAuthorizationService authorization)
{
    public async Task<ServiceResult<List<ManagedCredentialDto>>> GetAsync(string? searchText = null)
    {
        if (!await IsAdminAsync())
            return ServiceResult<List<ManagedCredentialDto>>.Fail(ErrorKeys.ManagedCredential.Forbidden);

        await using var db = await dbFactory.CreateContextAsync();
        var query = db.ManagedCredentials.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            var pattern = $"%{searchText.Trim()}%";
            query = query.Where(x => EF.Functions.ILike(x.Name, pattern) || EF.Functions.ILike(x.Username, pattern));
        }

        var items = await query.OrderBy(x => x.Name).ThenBy(x => x.Id)
            .Select(x => new ManagedCredentialDto { Id = x.Id, Name = x.Name, Username = x.Username, Guid = x.Guid })
            .ToListAsync();
        return ServiceResult<List<ManagedCredentialDto>>.Ok(items);
    }

    public async Task<ServiceResult<ManagedCredentialDto>> CreateAsync(ManagedCredentialDto input, string password)
    {
        if (!await IsAdminAsync())
            return ServiceResult<ManagedCredentialDto>.Fail(ErrorKeys.ManagedCredential.Forbidden);

        var name = input.Name?.Trim();
        var username = input.Username?.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(username))
            return ServiceResult<ManagedCredentialDto>.Fail(ErrorKeys.ManagedCredential.Validation, UiText.Get(LocKeys.Ui.ManagedCredentialAdministration.EnterANameAndUsername));
        if (string.IsNullOrWhiteSpace(password))
            return ServiceResult<ManagedCredentialDto>.Fail(ErrorKeys.ManagedCredential.Validation, UiText.Get(LocKeys.Ui.ManagedCredentialAdministration.EnterAPassword));

        await using var db = await dbFactory.CreateContextAsync();
        if (await db.ManagedCredentials.AnyAsync(x => x.Name == name))
            return ServiceResult<ManagedCredentialDto>.Fail(ErrorKeys.Common.AlreadyExists, name);

        var entity = new ManagedCredential
        {
            Name = name,
            Username = username,
            PasswordEncrypted = secretProtector.Protect(password)
        };
        db.ManagedCredentials.Add(entity);
        await db.SaveChangesAsync();
        return ServiceResult<ManagedCredentialDto>.Ok(ToDto(entity));
    }

    public async Task<ServiceResult<ManagedCredentialDto>> UpdateAsync(ManagedCredentialDto input, string? password)
    {
        if (!await IsAdminAsync())
            return ServiceResult<ManagedCredentialDto>.Fail(ErrorKeys.ManagedCredential.Forbidden);

        var name = input.Name?.Trim();
        var username = input.Username?.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(username))
            return ServiceResult<ManagedCredentialDto>.Fail(ErrorKeys.ManagedCredential.Validation, UiText.Get(LocKeys.Ui.ManagedCredentialAdministration.EnterANameAndUsername));

        await using var db = await dbFactory.CreateContextAsync();
        var entity = await db.ManagedCredentials.FindAsync(input.Id);
        if (entity is null)
            return ServiceResult<ManagedCredentialDto>.Fail(ErrorKeys.Common.NotFound, input.Id);
        if (await db.ManagedCredentials.AnyAsync(x => x.Id != input.Id && x.Name == name))
            return ServiceResult<ManagedCredentialDto>.Fail(ErrorKeys.Common.AlreadyExists, name);

        entity.Name = name;
        entity.Username = username;
        if (!string.IsNullOrWhiteSpace(password))
            entity.PasswordEncrypted = secretProtector.Protect(password);

        await db.SaveChangesAsync();
        return ServiceResult<ManagedCredentialDto>.Ok(ToDto(entity));
    }

    public Task<ServiceResult> DeleteAsync(int id) => DeleteManyAsync([id]);

    public async Task<ServiceResult> DeleteManyAsync(IEnumerable<int> ids)
    {
        if (!await IsAdminAsync())
            return ServiceResult.Fail(ErrorKeys.ManagedCredential.Forbidden);

        var selected = ids.Distinct().ToArray();
        if (selected.Length == 0)
            return ServiceResult.Ok();

        await using var db = await dbFactory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var entities = await db.ManagedCredentials.Where(x => selected.Contains(x.Id)).ToListAsync();
        if (entities.Count != selected.Length)
            return ServiceResult.Fail(ErrorKeys.Common.NotFound);
        if (await IsInUseAsync(db, selected))
            return ServiceResult.Fail(ErrorKeys.ManagedCredential.InUse);

        db.ManagedCredentials.RemoveRange(entities);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult.Ok();
    }

    private async Task<bool> IsAdminAsync()
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        return (await authorization.AuthorizeAsync(user, Constants.Permissions.AdminOnly)).Succeeded;
    }

    private static async Task<bool> IsInUseAsync(CloneDeployDbContext db, int[] ids) =>
        await ImagePrepUsesCredentialsAsync(db, ids) ||
        await db.StorageProviders.AnyAsync(x => x.ManagedCredentialId.HasValue && ids.Contains(x.ManagedCredentialId.Value)) ||
        await db.LdapDirectorySettings.AnyAsync(x => ids.Contains(x.ManagedCredentialId)) ||
        await db.CommandActions.AnyAsync(x => x.ManagedCredentialId.HasValue && ids.Contains(x.ManagedCredentialId.Value)) ||
        await db.ScriptActions.AnyAsync(x => x.ManagedCredentialId.HasValue && ids.Contains(x.ManagedCredentialId.Value)) ||
        await db.DomainJoinActions.AnyAsync(x => x.ManagedCredentialId.HasValue && ids.Contains(x.ManagedCredentialId.Value)) ||
        await db.SoftwareActions.AnyAsync(x => x.ManagedCredentialId.HasValue && ids.Contains(x.ManagedCredentialId.Value));

    private static async Task<bool> ImagePrepUsesCredentialsAsync(CloneDeployDbContext db, int[] ids)
    {
        var profiles = await db.ImagePrepProfiles.AsNoTracking().Where(x => x.GeneratorJson != null).ToListAsync();
        return profiles.Any(x => x.GeneratorOptions?.CredentialIds().Any(ids.Contains) == true);
    }

    private static ManagedCredentialDto ToDto(ManagedCredential entity) =>
        new() { Id = entity.Id, Name = entity.Name, Username = entity.Username, Guid = entity.Guid };

}