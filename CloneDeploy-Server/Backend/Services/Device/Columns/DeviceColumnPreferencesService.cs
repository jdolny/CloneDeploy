using System.Security.Claims;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Saves each user's device list columns and supplies the values shown in them.
/// </summary>
public sealed class DeviceColumnPreferencesService(
    ICloneDeployDbFactory dbFactory,
    AuthenticationStateProvider authentication,
    SharedResourceAccess access)
{
    private async Task<int?> GetUserIdAsync()
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        if (user.Identity?.IsAuthenticated != true ||
            !int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id))
            return null;
        return id;
    }

    public async Task<ServiceResult<List<AppUserGridColumn>>> LoadAsync()
    {
        var userId = await GetUserIdAsync();
        if (userId is null) return ServiceResult<List<AppUserGridColumn>>.Fail("Device.Forbidden");
        await using var db = await dbFactory.CreateContextAsync();
        return ServiceResult<List<AppUserGridColumn>>.Ok(await db.AppUserGridColumns.AsNoTracking()
            .Where(x => x.AppUserId == userId && x.GridKey == "devices")
            .OrderBy(x => x.Order).ThenBy(x => x.ColumnKey).ToListAsync());
    }

    // Ownership always comes from the authenticated user, never from submitted rows.
    public async Task<ServiceResult> SaveAsync(IReadOnlyList<AppUserGridColumn> columns)
    {
        if (columns.Any(x => string.IsNullOrWhiteSpace(x.ColumnKey) || x.ColumnKey.Length > 120) ||
            columns.Select(x => x.ColumnKey).Distinct().Count() != columns.Count)
            return ServiceResult.Fail("Device.ColumnsInvalid");
        var userId = await GetUserIdAsync();
        if (userId is null) return ServiceResult.Fail("Device.Forbidden");
        await using var db = await dbFactory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        if (!await access.AllowedAsync(Constants.Permissions.CustomAttributeView))
        {
            var existing = await db.AppUserGridColumns.Where(x => x.AppUserId == userId && x.GridKey == "devices")
                .Select(x => x.ColumnKey).ToListAsync();
            if (columns.Any(x => x.ColumnKey.StartsWith("custom-attribute:", StringComparison.Ordinal) && !existing.Contains(x.ColumnKey)))
                return ServiceResult.Fail("SharedResource.Forbidden");
        }
        await db.AppUserGridColumns.Where(x => x.AppUserId == userId && x.GridKey == "devices")
            .ExecuteDeleteAsync();
        db.AppUserGridColumns.AddRange(columns.Select((x, index) => new AppUserGridColumn
        {
            AppUserId = userId.Value, GridKey = "devices", ColumnKey = x.ColumnKey,
            Visible = x.Visible, Order = index
        }));
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult<List<CustomAttribute>>> GetCustomAttributesAsync()
    {
        var userId = await GetUserIdAsync();
        if (userId is null) return ServiceResult<List<CustomAttribute>>.Fail("Device.Forbidden");
        var canView = await access.AllowedAsync(Constants.Permissions.CustomAttributeView);
        await using var db = await dbFactory.CreateContextAsync();
        var selected = db.AppUserGridColumns.Where(x => x.AppUserId == userId && x.GridKey == "devices").Select(x => x.ColumnKey);
        return ServiceResult<List<CustomAttribute>>.Ok(await db.CustomAttributes.AsNoTracking()
            .Where(x => canView || selected.Contains("custom-attribute:" + x.Id)).OrderBy(x => x.Name).ToListAsync());
    }

    public async Task<ServiceResult> LoadAttributeValuesAsync(IEnumerable<DeviceSearchResultDto> devices, int[] attributeIds)
    {
        if (attributeIds.Length == 0) return ServiceResult.Ok();
        if (await GetUserIdAsync() is null) return ServiceResult.Fail("Device.Forbidden");
        var rows = devices.ToDictionary(x => x.Id);
        var deviceIds = rows.Keys.ToArray();
        await using var db = await dbFactory.CreateContextAsync();
        var values = await db.DeviceCustomAttributes.AsNoTracking()
            .Where(x => deviceIds.Contains(x.DeviceId) && attributeIds.Contains(x.CustomAttributeId))
            .OrderBy(x => x.Id).ToListAsync();
        foreach (var value in values)
            rows[value.DeviceId].CustomAttributeValues[value.CustomAttributeId] = value.Value;
        return ServiceResult.Ok();
    }
}
