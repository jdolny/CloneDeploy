using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Finds matching pages and records that the current user is allowed to access.
/// </summary>
public sealed class NavigationSearchQueries(
    ICloneDeployDbFactory dbFactory,
    AuthenticationStateProvider authentication,
    IAuthorizationService authorization,
    IAuthorizationPolicyProvider policyProvider)
{
    private const int ResultsPerCategory = 5;

    public async Task<ServiceResult<List<NavigationSearchResultDto>>> SearchAsync(string? text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        text = text?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length < 2)
            return ServiceResult<List<NavigationSearchResultDto>>.Ok([]);

        var user = (await authentication.GetAuthenticationStateAsync()).User;
        if (!(await authorization.AuthorizeAsync(user, null, await policyProvider.GetDefaultPolicyAsync())).Succeeded)
            return ServiceResult<List<NavigationSearchResultDto>>.Fail("NavigationSearch.Forbidden");

        var canSearchDevices = (await authorization.AuthorizeAsync(user, Constants.Permissions.DeviceView)).Succeeded;
        var canSearchUsers = (await authorization.AuthorizeAsync(user, Constants.Permissions.AdminOnly)).Succeeded;
        var canSearchPolicies = (await authorization.AuthorizeAsync(user, Constants.Permissions.PolicyView)).Succeeded;
        var canSearchImages = (await authorization.AuthorizeAsync(user, Constants.Permissions.ImageView)).Succeeded;
        ct.ThrowIfCancellationRequested();
        if (!canSearchDevices && !canSearchUsers && !canSearchPolicies && !canSearchImages)
            return ServiceResult<List<NavigationSearchResultDto>>.Ok([]);

        await using var db = await dbFactory.CreateContextAsync();
        // Treat LIKE metacharacters as literal characters typed by the user.
        var pattern = "%" + text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        var results = new List<NavigationSearchResultDto>();
        if (canSearchDevices)
        {
            var devices = await db.Devices.AsNoTracking()
                .Where(x => EF.Functions.ILike(x.Name, pattern, "\\"))
                .OrderBy(x => x.Name).ThenBy(x => x.Id).Take(ResultsPerCategory)
                .Select(x => new { x.Id, x.Name }).ToListAsync(ct);
            results.AddRange(devices.Select(x => new NavigationSearchResultDto(x.Name, "Devices", $"/devices/manage/{x.Id}")));
        }
        if (canSearchUsers)
        {
            var users = await db.Users.AsNoTracking()
                .Where(x => x.UserName != null && EF.Functions.ILike(x.UserName, pattern, "\\"))
                .OrderBy(x => x.UserName).ThenBy(x => x.Id).Take(ResultsPerCategory)
                .Select(x => new { x.Id, x.UserName }).ToListAsync(ct);
            results.AddRange(users.Select(x => new NavigationSearchResultDto(x.UserName!, "Application Users", $"/users/manage/{x.Id}")));
        }
        if (canSearchPolicies)
        {
            var policies = await db.Policies.AsNoTracking()
                .Where(x => EF.Functions.ILike(x.Name, pattern, "\\"))
                .OrderBy(x => x.Name).ThenBy(x => x.Id).Take(ResultsPerCategory)
                .Select(x => new { x.Id, x.Name }).ToListAsync(ct);
            results.AddRange(policies.Select(x => new NavigationSearchResultDto(x.Name, "Policies", $"/policies/{x.Id}")));

            var actionQuery = db.CommandActions.AsNoTracking().Select(x => new { x.Id, x.Name, Route = "command" })
                .Concat(db.DomainJoinActions.AsNoTracking().Select(x => new { x.Id, x.Name, Route = "domainjoin" }))
                .Concat(db.FileActions.AsNoTracking().Select(x => new { x.Id, x.Name, Route = "file" }))
                .Concat(db.NotificationActions.AsNoTracking().Select(x => new { x.Id, x.Name, Route = "notification" }))
                .Concat(db.PrinterActions.AsNoTracking().Select(x => new { x.Id, x.Name, Route = "printer" }))
                .Concat(db.ScriptActions.AsNoTracking().Select(x => new { x.Id, x.Name, Route = "script" }))
                .Concat(db.SoftwareActions.AsNoTracking().Select(x => new { x.Id, x.Name, Route = "software" }))
                .Concat(db.WindowsUpdateActions.AsNoTracking().Select(x => new { x.Id, x.Name, Route = "windowsupdate" }));
            var actions = await actionQuery.Where(x => EF.Functions.ILike(x.Name, pattern, "\\"))
                .OrderBy(x => x.Name).ThenBy(x => x.Route).ThenBy(x => x.Id).Take(ResultsPerCategory).ToListAsync(ct);
            results.AddRange(actions.Select(x => new NavigationSearchResultDto(x.Name, "Actions", $"/actions/{x.Route}/{x.Id}")));
        }
        if (canSearchUsers)
        {
            var directoryUsers = await db.DirectoryObjects.AsNoTracking()
                .Where(x => x.Kind == DirectoryObjectKind.User && !x.IsMissing &&
                    (EF.Functions.ILike(x.Name, pattern, "\\") ||
                     x.AccountName != null && EF.Functions.ILike(x.AccountName, pattern, "\\") ||
                     x.UserPrincipalName != null && EF.Functions.ILike(x.UserPrincipalName, pattern, "\\") ||
                     x.Email != null && EF.Functions.ILike(x.Email, pattern, "\\")))
                .OrderBy(x => x.Name).ThenBy(x => x.DirectorySourceId).ThenBy(x => x.ObjectGuid).Take(ResultsPerCategory)
                .Select(x => new { x.DirectorySourceId, x.ObjectGuid, x.Name, x.AccountName, Source = x.DirectorySource.Name }).ToListAsync(ct);
            results.AddRange(directoryUsers.Select(x => new NavigationSearchResultDto(
                $"{x.Name} ({x.AccountName ?? x.Name}) — {x.Source}", "Directory Users", $"/directory-users/{x.DirectorySourceId}/{x.ObjectGuid}")));
        }
        if (canSearchImages)
        {
            var images = await db.Images.AsNoTracking()
                .Where(x => EF.Functions.ILike(x.Name, pattern, "\\"))
                .OrderBy(x => x.Name).ThenBy(x => x.Id).Take(ResultsPerCategory)
                .Select(x => new { x.Id, x.Name }).ToListAsync(ct);
            results.AddRange(images.Select(x => new NavigationSearchResultDto(x.Name, "Images", $"/images/manage/{x.Id}")));
        }
        return ServiceResult<List<NavigationSearchResultDto>>.Ok(results);
    }
}
