using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Localization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Checks and saves custom field definitions and removes fields that are no longer needed.
/// </summary>
public sealed class CustomAttributeAdministration(ICloneDeployDbFactory dbFactory, SharedResourceAccess access)
{
    public Task<ServiceResult> DeleteAsync(int id) => DeleteManyAsync([id]);

    public async Task<ServiceResult> DeleteManyAsync(IEnumerable<int> ids)
    {
        if (!await access.AllowedAsync(Constants.Permissions.CustomAttributeEdit)) return ServiceResult.Fail("SharedResource.Forbidden");
        var idList = ids.Distinct().ToList();
        if (idList.Count == 0) return ServiceResult.Ok();

        await using var db = await dbFactory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var count = await db.CustomAttributes.Where(x => idList.Contains(x.Id)).ExecuteDeleteAsync();
        // Returning before commit rolls back the entire deletion, including cascaded values.
        if (count != idList.Count)
            return ServiceResult.Fail(ErrorKeys.Common.NotFound, string.Join(", ", idList));

        await transaction.CommitAsync();
        return ServiceResult.Ok();
    }

    public async Task<List<CustomAttribute>> GetAsync(string? searchText = null)
    {
        if (!await access.AllowedAsync(Constants.Permissions.CustomAttributeView)) throw new UnauthorizedAccessException();
        await using var db = await dbFactory.CreateContextAsync();
        var query = db.CustomAttributes.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            var pattern = $"%{searchText.Trim()}%";
            query = query.Where(x => EF.Functions.ILike(x.Name, pattern) ||
                (x.Description != null && EF.Functions.ILike(x.Description, pattern)));
        }

        return await query.OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync();
    }

    public async Task<ServiceResult<CustomAttribute>> CreateAsync(CustomAttribute input)
    {
        if (!await access.AllowedAsync(Constants.Permissions.CustomAttributeEdit)) return ServiceResult<CustomAttribute>.Fail("SharedResource.Forbidden");
        var name = input.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return ServiceResult<CustomAttribute>.Fail(ErrorKeys.CustomAttribute.Validation);

        await using var db = await dbFactory.CreateContextAsync();
        if (await db.CustomAttributes.AnyAsync(x => x.Name == name))
            return ServiceResult<CustomAttribute>.Fail(ErrorKeys.Common.AlreadyExists, name);

        var entity = new CustomAttribute();
        CopySettings(input, entity, name);
        db.CustomAttributes.Add(entity);
        await db.SaveChangesAsync();
        return ServiceResult<CustomAttribute>.Ok(entity);
    }

    public async Task<ServiceResult<CustomAttribute>> UpdateAsync(CustomAttribute input)
    {
        if (!await access.AllowedAsync(Constants.Permissions.CustomAttributeEdit)) return ServiceResult<CustomAttribute>.Fail("SharedResource.Forbidden");
        var name = input.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return ServiceResult<CustomAttribute>.Fail(ErrorKeys.CustomAttribute.Validation);

        await using var db = await dbFactory.CreateContextAsync();
        var entity = await db.CustomAttributes.SingleOrDefaultAsync(x => x.Id == input.Id);
        if (entity is null)
            return ServiceResult<CustomAttribute>.Fail(ErrorKeys.Common.NotFound, input.Id);
        if (await db.CustomAttributes.AnyAsync(x => x.Id != input.Id && x.Name == name))
            return ServiceResult<CustomAttribute>.Fail(ErrorKeys.Common.AlreadyExists, name);

        CopySettings(input, entity, name);
        await db.SaveChangesAsync();
        return ServiceResult<CustomAttribute>.Ok(entity);
    }

    private static void CopySettings(CustomAttribute source, CustomAttribute target, string name)
    {
        target.Name = name;
        target.Description = source.Description;
        target.ClientImagingAvailable = source.ClientImagingAvailable;
    }
}
