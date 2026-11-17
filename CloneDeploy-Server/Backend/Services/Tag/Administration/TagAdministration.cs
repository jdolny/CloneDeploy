using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Localization;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Checks and saves labels and prevents deletion while they are still in use.
/// </summary>
public sealed class TagAdministration(ICloneDeployDbFactory dbFactory, SharedResourceAccess access)
{
    public async Task<ServiceResult<List<Tag>>> GetAsync(string? searchText = null)
    {
        if (!await access.AllowedAsync(Constants.Permissions.TagView)) return ServiceResult<List<Tag>>.Fail("SharedResource.Forbidden");
        await using var db = await dbFactory.CreateContextAsync();
        var query = db.Tags.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            var pattern = $"%{searchText.Trim()}%";
            query = query.Where(x => EF.Functions.ILike(x.Name, pattern) ||
                                     (x.Description != null && EF.Functions.ILike(x.Description, pattern)));
        }

        return ServiceResult<List<Tag>>.Ok(await query.OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync());
    }

    public async Task<ServiceResult<Tag>> CreateAsync(Tag input)
    {
        if (!await access.AllowedAsync(Constants.Permissions.TagEdit)) return ServiceResult<Tag>.Fail("SharedResource.Forbidden");
        var name = input.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return ServiceResult<Tag>.Fail(ErrorKeys.Tag.Validation);

        await using var db = await dbFactory.CreateContextAsync();
        if (await db.Tags.AnyAsync(x => x.Name == name))
            return ServiceResult<Tag>.Fail(ErrorKeys.Common.AlreadyExists, name);

        var entity = new Tag();
        CopySettings(input, entity, name);
        db.Tags.Add(entity);
        await db.SaveChangesAsync();
        return ServiceResult<Tag>.Ok(entity);
    }

    public async Task<ServiceResult<Tag>> UpdateAsync(Tag input)
    {
        if (!await access.AllowedAsync(Constants.Permissions.TagEdit)) return ServiceResult<Tag>.Fail("SharedResource.Forbidden");
        var name = input.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return ServiceResult<Tag>.Fail(ErrorKeys.Tag.Validation);

        await using var db = await dbFactory.CreateContextAsync();
        var entity = await db.Tags.FindAsync(input.Id);
        if (entity is null)
            return ServiceResult<Tag>.Fail(ErrorKeys.Common.NotFound, input.Id);
        if (await db.Tags.AnyAsync(x => x.Id != input.Id && x.Name == name))
            return ServiceResult<Tag>.Fail(ErrorKeys.Common.AlreadyExists, name);

        CopySettings(input, entity, name);
        await db.SaveChangesAsync();
        return ServiceResult<Tag>.Ok(entity);
    }

    public Task<ServiceResult> DeleteAsync(int id) => DeleteManyAsync([id]);

    public async Task<ServiceResult> DeleteManyAsync(IEnumerable<int> ids)
    {
        if (!await access.AllowedAsync(Constants.Permissions.TagEdit)) return ServiceResult.Fail("SharedResource.Forbidden");
        var selected = ids.Distinct().ToArray();
        if (selected.Length == 0)
            return ServiceResult.Ok();

        await using var db = await dbFactory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var entities = await db.Tags.Where(x => selected.Contains(x.Id)).ToListAsync();
        if (entities.Count != selected.Length)
            return ServiceResult.Fail(ErrorKeys.Common.NotFound);

        db.Tags.RemoveRange(entities);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult.Ok();
    }

    private static void CopySettings(Tag input, Tag entity, string name)
    {
        entity.Name = name;
        entity.Description = input.Description?.Trim();
    }

}
