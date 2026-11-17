using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services;

// Null means unrestricted; an empty selection deliberately matches no tickets.
public sealed record HelpTicketListFilter(int[]? QueueIds, int[]? CategoryIds, HelpTicketStatus[]? Statuses)
{
    public IQueryable<HelpTicket> Apply(IQueryable<HelpTicket> query)
    {
        if (QueueIds is { } queues) query = query.Where(x => queues.Contains(x.QueueId ?? 0));
        if (CategoryIds is { } categories) query = query.Where(x => categories.Contains(x.CategoryId ?? 0));
        if (Statuses is { } statuses) query = query.Where(x => statuses.Contains(x.Status));
        return query;
    }
}
