using System.Security.Claims;
using System.Text.Json;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed record HelpRoutingSettings(HelpCategory[] Categories, HelpQueue[] Queues, HelpRoutingRule[] Rules);

public sealed class HelpRoutingService(ICloneDeployDbFactory factory, IAuthorizationService authorization, HelpDeskService help)
{
    private async Task Admin(ClaimsPrincipal user)
    {
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.AdminOnly)).Succeeded) throw new UnauthorizedAccessException();
    }

    public async Task<HelpRoutingSettings> ReadAsync(ClaimsPrincipal user)
    {
        await Admin(user);
        await using var db = await factory.CreateContextAsync();
        return new(await db.Set<HelpCategory>().AsNoTracking().OrderBy(x => x.Name).ToArrayAsync(),
            await db.Set<HelpQueue>().AsNoTracking().Include(x => x.Members).OrderBy(x => x.Name).ToArrayAsync(),
            await db.Set<HelpRoutingRule>().AsNoTracking().OrderBy(x => x.Order).ThenBy(x => x.Id).ToArrayAsync());
    }

    public async Task SaveCategoryAsync(ClaimsPrincipal user, int id, string name)
    {
        await Admin(user);
        name = Name(name);
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(682947240, 1)");
        if (await db.Set<HelpCategory>().AnyAsync(x => x.Id != id && x.Name.ToLower() == name.ToLower())) throw new InvalidOperationException("A category with that name already exists.");
        var row = id == 0 ? new HelpCategory() : await db.Set<HelpCategory>().SingleAsync(x => x.Id == id);
        row.Name = name;
        if (id == 0) db.Add(row);
        Audit(db, user, "HelpCategory", id, name);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    public Task SaveQueueAsync(ClaimsPrincipal user, int id, string name, int[] memberIds) => SaveQueueAsync(user, id, name, memberIds, null);
    public async Task SaveQueueAsync(ClaimsPrincipal user, int id, string name, int[] memberIds, bool? emailEnabled)
    {
        await Admin(user);
        name = Name(name);
        var eligible = (await help.StaffAsync(user)).Where(x => x.ManageAll ||
            (id == HelpQueue.UnassignedId ? x.ManageUnassigned : x.ManageAssigned));
        if (memberIds.Except(eligible.Select(x => x.Id)).Any()) throw new InvalidOperationException("Select active application users with permission to manage this queue.");
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(682947240, 1)");
        if (await db.Set<HelpQueue>().AnyAsync(x => x.Id != id && x.Name.ToLower() == name.ToLower())) throw new InvalidOperationException("A queue with that name already exists.");
        var row = id == 0 ? new HelpQueue() : await db.Set<HelpQueue>().Include(x => x.Members).SingleAsync(x => x.Id == id);
        if (id == HelpQueue.UnassignedId && name != HelpQueue.UnassignedName)
            throw new InvalidOperationException("The built-in Unassigned queue cannot be renamed.");
        if (id != HelpQueue.UnassignedId && name.Equals(HelpQueue.UnassignedName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unassigned is reserved for the built-in queue.");
        row.Name = name;
        row.EmailEnabled = emailEnabled ?? row.EmailEnabled;
        foreach (var old in row.Members.Where(x => !memberIds.Contains(x.UserId)).ToArray()) db.Remove(old);
        foreach (var member in memberIds.Distinct().Where(x => row.Members.All(m => m.UserId != x))) row.Members.Add(new() { UserId = member });
        if (id == 0) db.Add(row);
        Audit(db, user, "HelpQueue", id, name, new { memberIds, row.EmailEnabled });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    public async Task SaveRuleAsync(ClaimsPrincipal user, HelpRoutingRule input)
    {
        await Admin(user);
        input.Name = Name(input.Name);
        if (!System.Enum.IsDefined(input.Stage) || input.Order < 0 || input.Order > 100000) throw new InvalidOperationException("Select a rule stage and an order between 0 and 100000.");
        HelpRuleCondition[] conditions;
        try { conditions = HelpRoutingEngine.Conditions(input); }
        catch (JsonException) { throw new InvalidOperationException("Invalid rule conditions."); }
        if (conditions.Length > 20) throw new InvalidOperationException("Use at most 20 conditions per rule.");
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(682947240, 1)");
        foreach (var c in conditions)
        {
            if (c is null || !System.Enum.IsDefined(c.Field) || !System.Enum.IsDefined(c.Operator) || string.IsNullOrWhiteSpace(c.Value) || c.Value.Length > 500)
                throw new InvalidOperationException("Every condition requires a field, operator, and value of up to 500 characters.");
            if (c.Field == HelpRuleField.Category)
            {
                if (input.Stage != HelpRuleStage.Assignment || c.Operator != HelpRuleOperator.Equals || !int.TryParse(c.Value, out var categoryId) || !await db.Set<HelpCategory>().AnyAsync(x => x.Id == categoryId))
                    throw new InvalidOperationException("Select an existing category for assignment conditions.");
            }
            else if (c.Field == HelpRuleField.DeviceIp)
            {
                if (c.Operator != HelpRuleOperator.InRange || !HelpRoutingEngine.ValidRange(c.Value)) throw new InvalidOperationException("Enter an IP subnet (10.20.0.0/16) or range (10.20.0.1-10.20.0.254).");
            }
            else if (HelpRoutingEngine.IsDirectory(c.Field))
            {
                var kind = HelpRoutingEngine.IsOu(c.Field) ? DirectoryObjectKind.OrganizationalUnit : DirectoryObjectKind.Group;
                if (input.Stage != HelpRuleStage.Assignment || c.Operator != HelpRuleOperator.Equals ||
                    !HelpRoutingDirectory.TryKey(c.Value, out var sourceId, out var objectId) ||
                    !await HelpRoutingDirectory.Active(db).AnyAsync(x => x.DirectorySourceId == sourceId && x.ObjectGuid == objectId && x.Kind == kind))
                    throw new InvalidOperationException("Select an available directory group or OU for queue assignment.");
            }
            else if (c.Operator == HelpRuleOperator.Regex && HelpRoutingEngine.RegexError(c.Value) is { } regexError)
                throw new InvalidOperationException(regexError);
            else if (c.Operator == HelpRuleOperator.InRange) throw new InvalidOperationException("IP range matching is only available for device IP conditions.");
        }
        if (input.Stage == HelpRuleStage.Categorization)
        {
            if (!await db.Set<HelpCategory>().AnyAsync(x => x.Id == input.CategoryId)) throw new InvalidOperationException("Select a category to apply.");
            input.QueueId = null;
        }
        else
        {
            if (!await db.Set<HelpQueue>().AnyAsync(x => x.Id == input.QueueId)) throw new InvalidOperationException("Select a queue to apply.");
            input.CategoryId = null;
        }
        var row = input.Id == 0 ? new HelpRoutingRule() : await db.Set<HelpRoutingRule>().SingleAsync(x => x.Id == input.Id);
        row.Name = input.Name; row.Stage = input.Stage; row.Order = input.Order; row.Enabled = input.Enabled;
        row.MatchAll = input.MatchAll; row.ConditionsJson = JsonSerializer.Serialize(conditions.Select(c => c with { Value = c.Operator == HelpRuleOperator.Regex ? c.Value : c.Value.Trim() }));
        row.CategoryId = input.CategoryId; row.QueueId = input.QueueId;
        if (input.Id == 0) db.Add(row);
        Audit(db, user, "HelpRoutingRule", row.Id, row.Name, row);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    public async Task DeleteAsync(ClaimsPrincipal user, string kind, int id)
    {
        await Admin(user);
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(682947240, 1)");
        object? row;
        if (kind == "category")
        {
            var rules = await db.Set<HelpRoutingRule>().AsNoTracking().ToArrayAsync();
            if (rules.Any(x => x.CategoryId == id || HelpRoutingEngine.Conditions(x).Any(c => c.Field == HelpRuleField.Category && c.Value == id.ToString())) || await db.HelpTickets.AnyAsync(x => x.CategoryId == id))
                throw new InvalidOperationException("This category is used by tickets or rules. Remove those references before deleting it.");
            row = await db.Set<HelpCategory>().FindAsync(id);
        }
        else if (kind == "queue")
        {
            if (id == HelpQueue.UnassignedId) throw new InvalidOperationException("The built-in Unassigned queue cannot be deleted.");
            if (await db.Set<HelpRoutingRule>().AnyAsync(x => x.QueueId == id) || await db.HelpTickets.AnyAsync(x => x.QueueId == id))
                throw new InvalidOperationException("This queue is used by tickets or rules. Remove those references before deleting it.");
            row = await db.Set<HelpQueue>().FindAsync(id);
        }
        else if (kind == "rule") row = await db.Set<HelpRoutingRule>().FindAsync(id);
        else throw new InvalidOperationException("Unknown configuration type.");
        if (row is null) return;
        db.Remove(row);
        Audit(db, user, "HelpRoutingConfiguration", id, kind, new { Deleted = true });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    public async Task<HelpRoutingResult> PreviewAsync(ClaimsPrincipal user, HelpRoutingInput input)
    {
        var settings = await ReadAsync(user);
        await using var db = await factory.CreateContextAsync();
        async Task<DirectoryObject[]> Subject(string? key, DirectoryObjectKind kind)
        {
            if (!HelpRoutingDirectory.TryKey(key, out var source, out var guid)) return [];
            return await HelpRoutingDirectory.Active(db).Where(x => x.DirectorySourceId == source && x.ObjectGuid == guid && x.Kind == kind).ToArrayAsync();
        }
        var resolved = await HelpRoutingDirectory.PopulateAsync(db, input, await Subject(input.UserDirectoryKey, DirectoryObjectKind.User),
            await Subject(input.DeviceDirectoryKey, DirectoryObjectKind.Computer), default);
        return HelpRoutingEngine.Evaluate(settings.Rules, resolved);
    }

    public async Task<HelpDirectoryChoice[]> SearchDirectoryAsync(ClaimsPrincipal user, DirectoryObjectKind kind, string? search, CancellationToken ct)
    {
        await Admin(user);
        await using var db = await factory.CreateContextAsync();
        var query = HelpRoutingDirectory.Active(db).Where(x => x.Kind == kind);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            query = query.Where(x => x.Name.ToLower().Contains(term) || x.DistinguishedName.ToLower().Contains(term) ||
                x.DirectorySource.Name.ToLower().Contains(term));
        }
        return await query.OrderBy(x => x.Name).ThenBy(x => x.DirectorySourceId).Take(50)
            .Select(x => new HelpDirectoryChoice(HelpRoutingDirectory.Key(x.DirectorySourceId, x.ObjectGuid),
                x.DirectorySource.Name + " / " + x.Name + " — " + x.DistinguishedName)).ToArrayAsync(ct);
    }
    public async Task<HelpDirectoryChoice?> DirectoryChoiceAsync(ClaimsPrincipal user, string key)
    {
        await Admin(user);
        if (!HelpRoutingDirectory.TryKey(key, out var source, out var guid)) return null;
        await using var db = await factory.CreateContextAsync();
        return await HelpRoutingDirectory.Active(db).Where(x => x.DirectorySourceId == source && x.ObjectGuid == guid)
            .Select(x => new HelpDirectoryChoice(key, x.DirectorySource.Name + " / " + x.Name + " — " + x.DistinguishedName)).SingleOrDefaultAsync();
    }

    private static string Name(string name) => !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= 100
        ? name.Trim() : throw new InvalidOperationException("Enter a name of up to 100 characters.");

    private static void Audit(CloneDeployDbContext db, ClaimsPrincipal user, string type, int id, string name, object? details = null) =>
        db.AuditLogs.Add(new AuditLog { AppUserId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!), UserName = user.Identity?.Name ?? "",
            AuditType = AuditType.Update, ObjectType = type, ObjectId = id, ObjectName = name, LoggedAtUtc = DateTime.UtcNow,
            ObjectJson = JsonSerializer.Serialize(details) });
}
