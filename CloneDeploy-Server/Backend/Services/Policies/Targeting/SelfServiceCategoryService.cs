using System.Security.Claims;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
namespace CloneDeploy_Server.Backend.Services;

/// <summary>Manages portal categories with category permissions, stale-edit protection and audit history.</summary>
public sealed class SelfServiceCategoryService(ICloneDeployDbFactory factory, AuthenticationStateProvider authentication, IAuthorizationService authorization)
{
    private async Task<ClaimsPrincipal?> UserAsync(string permission)
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        return (await authorization.AuthorizeAsync(user, permission)).Succeeded ? user : null;
    }
    public async Task<ServiceResult<List<SelfServiceCategory>>> ListAsync()
    {
        if (await UserAsync(Constants.Permissions.SelfServiceCategoryView) is null) return ServiceResult<List<SelfServiceCategory>>.Fail("SharedResource.Forbidden");
        await using var db = await factory.CreateContextAsync();
        return ServiceResult<List<SelfServiceCategory>>.Ok(await db.Set<SelfServiceCategory>().AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.Name).ThenBy(x => x.Id).ToListAsync());
    }
    public async Task<ServiceResult> SaveAsync(SelfServiceCategory input, bool delete = false)
    {
        var user = await UserAsync(Constants.Permissions.SelfServiceCategoryEdit);
        if (user is null) return ServiceResult.Fail("SharedResource.Forbidden");
        var name = input.Name.Trim();
        if (!delete && (name.Length is < 1 or > 80 || !PortalCategoryIcons.Keys.Contains(input.Icon)))
            return ServiceResult.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.SelfServiceCategoryService.EnterAName180CharactersAndAListed));
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(682947235)");
        var category = input.Id == 0 ? null : await db.Set<SelfServiceCategory>().SingleOrDefaultAsync(x => x.Id == input.Id);
        if (input.Id != 0 && (category is null || category.Revision != input.Revision))
            return ServiceResult.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.SelfServiceCategoryService.ThisCategoryChangedOrWasRemovedReloadBeforeSaving));
        if (!delete && await db.Set<SelfServiceCategory>().AnyAsync(x => x.Id != input.Id && x.Name.ToLower() == name.ToLower()))
            return ServiceResult.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.SelfServiceCategoryService.ACategoryWithThisNameAlreadyExists));
        var creating = category is null;
        if (delete)
        {
            if (category is null) return ServiceResult.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.SelfServiceCategoryService.CategoryNotFound));
            db.Remove(category); // FK SET NULL puts its policies under Other, without changing access.
        }
        else
        {
            if (category is null)
            {
                var lastOrder = await db.Set<SelfServiceCategory>().MaxAsync(x => (int?)x.SortOrder) ?? -1;
                category = new() { SortOrder = checked(lastOrder + 1) };
                db.Add(category);
            }
            category.Name = name; category.Icon = input.Icon; category.Revision = Guid.NewGuid();
        }
        await db.SaveChangesAsync();
        db.AuditLogs.Add(new AuditLog { AppUserId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!), UserName = user.Identity?.Name ?? "",
            AuditType = AuditType.Update, ObjectType = nameof(SelfServiceCategory), ObjectId = category!.Id, ObjectName = category.Name,
            ObjectJson = JsonSerializer.Serialize(new { Operation = delete ? "Delete" : creating ? "Create" : "Update", category.Name, category.Icon, category.SortOrder }) });
        await db.SaveChangesAsync(); await tx.CommitAsync();
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> MoveAsync(int id, Guid revision, int direction)
    {
        var user = await UserAsync(Constants.Permissions.SelfServiceCategoryEdit);
        if (user is null) return ServiceResult.Fail("SharedResource.Forbidden");
        if (direction is not (-1 or 1)) return ServiceResult.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.SelfServiceCategoryService.ChooseUpOrDown));
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(682947235)");
        var ordered = await db.Set<SelfServiceCategory>().OrderBy(x => x.SortOrder).ThenBy(x => x.Name).ThenBy(x => x.Id).ToListAsync();
        var index = ordered.FindIndex(x => x.Id == id);
        if (index < 0 || ordered[index].Revision != revision)
            return ServiceResult.Fail("Policy.Validation", UiText.Get(LocKeys.Ui.SelfServiceCategoryService.ThisCategoryChangedOrWasRemovedReloadBeforeReordering));
        var next = index + direction;
        if (next < 0 || next >= ordered.Count) return ServiceResult.Ok();
        var category = ordered[index];
        (ordered[index], ordered[next]) = (ordered[next], ordered[index]);
        // Normalize legacy ties and gaps so moving adjacent rows always changes their display order.
        for (var position = 0; position < ordered.Count; position++)
        {
            var item = ordered[position];
            if (item.SortOrder == position && position != index && position != next) continue;
            item.SortOrder = position;
            item.Revision = Guid.NewGuid();
        }
        db.AuditLogs.Add(new AuditLog
        {
            AppUserId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!), UserName = user.Identity?.Name ?? "",
            AuditType = AuditType.Update, ObjectType = nameof(SelfServiceCategory), ObjectId = category.Id, ObjectName = category.Name,
            ObjectJson = JsonSerializer.Serialize(new { Operation = "Reorder", From = index + 1, To = next + 1, CategoryIds = ordered.Select(x => x.Id).ToArray() })
        });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return ServiceResult.Ok();
    }
}
