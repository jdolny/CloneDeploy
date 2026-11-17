using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Localization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed class FirstAdminSetup(IServiceScopeFactory scopes, ILogger<FirstAdminSetup> logger)
{
    public Task<ServiceResult<AppUser>> CreateAsync(string? username, string password) => ServiceOperations.RunAsync(async () =>
    {
        // Identity and the transaction must use the same fresh scoped DbContext.
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CloneDeployDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(682947235, 1)");
        if (await db.Users.AnyAsync()) return ServiceResult<AppUser>.Fail(LocKeys.User.SetupUnavailable);
        var result = await scope.ServiceProvider.GetRequiredService<AppUserAdministration>().CreateAsync(
            new AppUser { UserName = username, Membership = Constants.UserMembership.Admin, LockoutEnabled = true }, password);
        if (result.Success) await transaction.CommitAsync();
        return result;
    }, logger);
}
