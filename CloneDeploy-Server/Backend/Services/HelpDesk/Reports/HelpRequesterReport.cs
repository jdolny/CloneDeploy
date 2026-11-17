using System.Security.Claims;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed record HelpRequesterReportRow(string Name, string? Username, int Total, int Open, int Closed,
    double Percentage, string[] Keys, int? FallbackTicketId);
public sealed record HelpRequesterTicket(int Id, string[] Keys, string Name, HelpTicketStatus Status, DateTime CreatedAtUtc);
public sealed record HelpRequesterDirectoryIdentity(string[] Keys, string Name, string? Username, bool Preferred);

public sealed partial class HelpDeskReportService
{
    public async Task<HelpRequesterReportRow[]> RequestersAsync(ClaimsPrincipal user, HelpReportFilter filter, CancellationToken ct = default)
    {
        Validate(filter);
        var access = await help.ReportAccessAsync(user);
        await using var db = await factory.CreateContextAsync();
        var tickets = await Filter(HelpDeskService.Visible(db.HelpTickets.AsNoTracking(), access), filter)
            .Where(x => x.CreatedAtUtc >= filter.FromUtc && x.CreatedAtUtc < filter.UntilUtc)
            .Select(x => new HelpRequesterTicket(x.Id, x.RequesterKeys, x.RequesterName, x.Status, x.CreatedAtUtc)).ToArrayAsync(ct);
        if (tickets.Length == 0) return [];
        var keys = tickets.SelectMany(x => x.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var guids = keys.Select(x => x.Split(':')).Where(x => x.Length == 3 && (x[0] == "ldap" || x[0] == "entra"))
            .Select(x => Guid.TryParse(x[2], out var guid) ? guid : Guid.Empty).Where(x => x != Guid.Empty).Distinct().ToArray();
        var sids = keys.Where(x => x.StartsWith("sid:", StringComparison.Ordinal)).Select(x => x[4..]).ToArray();
        // Missing/disabled accounts intentionally remain eligible for historical
        // labels. Neither names nor usernames are ever used to merge identities.
        var users = await db.DirectoryObjects.AsNoTracking().Include(x => x.DirectorySource).ThenInclude(x => x.SyncState)
            .Where(x => x.Kind == DirectoryObjectKind.User && (guids.Contains(x.ObjectGuid) || sids.Contains(x.ObjectSid!))).ToArrayAsync(ct);
        var tenant = await db.EntraConnections.Select(x => x.TenantId).SingleOrDefaultAsync(ct);
        var identities = users.Select(x =>
        {
            var aliases = new List<string>();
            if (x.DirectorySource.Provider == DirectoryProvider.EntraId)
            {
                var domain = x.DirectorySource.SyncState?.RootObjectGuid ?? tenant;
                if (domain != Guid.Empty) aliases.Add($"entra:{domain:N}:{x.ObjectGuid:N}");
            }
            else aliases.Add($"ldap:{x.DirectorySourceId}:{x.ObjectGuid:N}");
            if (x.ObjectSid?.StartsWith("S-1-5-21-", StringComparison.Ordinal) == true) aliases.Add("sid:" + x.ObjectSid);
            return new HelpRequesterDirectoryIdentity(aliases.ToArray(), x.Name,
                x.UserPrincipalName ?? x.AccountName, !x.DirectorySource.ApplicationAccessOnly && x.DirectorySource.Provider != DirectoryProvider.EntraId);
        }).ToArray();
        return HelpRequesterGrouping.Build(tickets, identities);
    }
}

public static class HelpRequesterGrouping
{
    public static HelpRequesterReportRow[] Build(HelpRequesterTicket[] tickets, HelpRequesterDirectoryIdentity[] identities)
    {
        // Shared stable aliases connect portal and agent requests, including
        // historical tickets whose directory account has since disappeared.
        var parents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string Find(string key)
        {
            if (!parents.TryGetValue(key, out var parent)) return parents[key] = key;
            var root = parent;
            while (!string.Equals(parents[root], root, StringComparison.OrdinalIgnoreCase)) root = parents[root];
            parents[key] = root;
            return root;
        }
        void Connect(string[] keys)
        {
            if (keys.Length == 0) return;
            var root = Find(keys[0]);
            foreach (var key in keys.Skip(1)) parents[Find(key)] = root;
        }
        foreach (var ticket in tickets) Connect(ticket.Keys);
        foreach (var identity in identities) Connect(identity.Keys);
        var labels = identities.Where(x => x.Keys.Length > 0).GroupBy(x => Find(x.Keys[0]), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(i => i.Preferred)
                .ThenBy(i => i.Keys[0], StringComparer.Ordinal).First(), StringComparer.OrdinalIgnoreCase);
        return tickets.GroupBy(x => x.Keys.Length == 0 ? $"ticket:{x.Id}" : Find(x.Keys[0]))
            .Select(group =>
            {
                var latest = group.OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id).First();
                var directory = labels.GetValueOrDefault(group.Key);
                var aliases = group.SelectMany(x => x.Keys).Distinct(StringComparer.Ordinal).ToArray();
                var total = group.Count();
                var closed = group.Count(x => x.Status == HelpTicketStatus.Closed);
                return new HelpRequesterReportRow(string.IsNullOrWhiteSpace(directory?.Name) ? latest.Name : directory.Name, directory?.Username, total, total - closed,
                    closed, tickets.Length == 0 ? 0 : 100d * total / tickets.Length, aliases, aliases.Length == 0 ? latest.Id : null);
            }).OrderByDescending(x => x.Total).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
