using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.EntityFrameworkCore;
namespace CloneDeploy_Server.Backend.Services.Scheduler;
public sealed class EmailDigestTaskHandler(ICloneDeployDbFactory factory, EmailDigestRunner runner) : IScheduledTaskHandler
{
    public ScheduledTaskType Type => ScheduledTaskType.EmailDigests;
    public string Name => "Email digest delivery";
    public string Description => "Checks opted-in daily and weekly subscriptions and delivers digests due in each user's timezone.";
    public async Task<List<int>> GetTargetsAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        if (!await db.Set<EmailServerSettings>().AnyAsync(x => x.Id == 1 && x.Enabled, ct)) return [];
        return await db.Set<UserEmailSubscription>().Where(x => (x.Daily || x.Weekly) && db.AppUsers.Any(u => u.Id == x.AppUserId && u.Email != null && u.Email != "")).OrderBy(x => x.AppUserId).Select(x => x.AppUserId).ToListAsync(ct);
    }
    public async Task<string?> GetTargetNameAsync(int id, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        return await db.AppUsers.Where(x => x.Id == id).Select(x => x.UserName).SingleOrDefaultAsync(ct);
    }
    public Task RunAsync(int targetId, CancellationToken ct) => runner.RunAsync(targetId, ct);
}
