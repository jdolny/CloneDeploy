using System.Buffers.Binary;
using System.Security.Claims;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Auth;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed record HelpStaff(int Id, string Name)
{
    public bool ManageAssigned { get; init; }
    public bool ManageAll { get; init; }
    public bool ManageUnassigned { get; init; }
}

public sealed class HelpDeskService(ICloneDeployDbFactory factory, IAuthorizationService authorization,
    IUserClaimsPrincipalFactory<AppUser> principals, TimeProvider clock)
{
    public const int ScreenshotLimit = HelpAttachmentLimits.ScreenshotBytes;
    public sealed record Access(int UserId, bool ViewAll, bool ViewAssigned, bool ViewUnassigned, bool Manage);
    public sealed record Overview(int New, int InProgress, int Waiting, int Closed, int Assigned);
    public async Task<Access> AccessAsync(ClaimsPrincipal user)
    {
        if (!int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id)) throw new UnauthorizedAccessException();
        async Task<bool> Has(string permission) => (await authorization.AuthorizeAsync(user, permission)).Succeeded;
        var access = new Access(id, await Has(Constants.Permissions.HelpDeskViewAll),
            await Has(Constants.Permissions.HelpDeskViewAssigned), await Has(Constants.Permissions.HelpDeskViewUnassigned),
            true);
        if (!access.ViewAll && !access.ViewAssigned && !access.ViewUnassigned) throw new UnauthorizedAccessException();
        return access;
    }
    internal async Task<Access> ReportAccessAsync(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true || !int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ||
            !(await authorization.AuthorizeAsync(user, Constants.Permissions.ReportHelpDesk)).Succeeded)
            throw new UnauthorizedAccessException();
        // Report access does not expand the technician's ticket visibility.
        async Task<bool> Has(string permission) => (await authorization.AuthorizeAsync(user, permission)).Succeeded;
        return new Access(id, await Has(Constants.Permissions.HelpDeskViewAll),
            await Has(Constants.Permissions.HelpDeskViewAssigned), await Has(Constants.Permissions.HelpDeskViewUnassigned), false);
    }

    internal static IQueryable<HelpTicket> Visible(IQueryable<HelpTicket> query, Access access) => query.Where(x =>
        access.ViewAll || access.ViewAssigned && (x.AssignedToId == access.UserId ||
            x.QueueId != HelpQueue.UnassignedId && x.Queue != null && x.Queue.Members.Any(m => m.UserId == access.UserId)) ||
        access.ViewUnassigned && x.QueueId == HelpQueue.UnassignedId);

    public async Task<Overview> OverviewAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        var access = await AccessAsync(user);
        await using var db = await factory.CreateContextAsync();
        var query = Visible(db.HelpTickets.AsNoTracking(), access);
        var cutoff = clock.GetUtcNow().UtcDateTime.AddDays(-30);
        return new(await query.CountAsync(x => x.Status == HelpTicketStatus.New, ct),
            await query.CountAsync(x => x.Status == HelpTicketStatus.InProgress, ct),
            await query.CountAsync(x => x.Status == HelpTicketStatus.WaitingOnUser, ct),
            await query.CountAsync(x => x.Status == HelpTicketStatus.Closed && x.UpdatedAtUtc >= cutoff, ct),
            await query.CountAsync(x => x.AssignedToId == access.UserId && x.Status != HelpTicketStatus.Closed, ct));
    }
    private static IQueryable<HelpTicket> Owned(CloneDeployDbContext db, string[] keys) =>
        db.HelpTickets.Where(t => t.RequesterKeys.Any(k => keys.Contains(k)));

    internal static async Task<(string[] Keys, string Name)> Identity(CloneDeployDbContext db, string sid, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sid) || sid.Length > 184) throw new InvalidOperationException(UiText.Get(LocKeys.Ui.HelpDeskService.ARecognizedWorkAccountIsRequiredToUseHelp));
        var (status, users) = await DirectoryIdentity.UsersAsync(db, sid, ct);
        if (status != "Matched") throw new InvalidOperationException(UiText.Get(LocKeys.Ui.HelpDeskService.HelpRequiresAnActiveWorkAccountRecognizedByA));
        var keys = new List<string>();
        foreach (var user in users)
        {
            if (user.DirectorySource.Provider == DirectoryProvider.EntraId)
            {
                var tenant = await db.EntraConnections.Select(x => x.TenantId).SingleOrDefaultAsync(ct);
                if (tenant == Guid.Empty) throw new InvalidOperationException(UiText.Get(LocKeys.Ui.HelpDeskService.TheEntraTenantIsNotConfigured));
                keys.Add($"entra:{tenant:N}:{user.ObjectGuid:N}");
            }
            else keys.Add($"ldap:{user.DirectorySourceId}:{user.ObjectGuid:N}");
            if (user.ObjectSid?.StartsWith("S-1-5-21-", StringComparison.Ordinal) == true) keys.Add("sid:" + user.ObjectSid);
        }
        return (keys.Distinct().ToArray(), users[0].Name);
    }

    public async Task<HelpResponse> AgentAsync(int deviceId, string sid, HelpRequest request, CancellationToken ct)
    {
        if (!System.Enum.IsDefined(request.Operation) || request.Offset < 0 || request.Offset > 100000 ||
            request.StatusFilter is { } filter && !System.Enum.IsDefined(filter))
            throw new InvalidOperationException(UiText.Get(LocKeys.Ui.HelpDeskService.InvalidHelpRequest));
        await using var db = await factory.CreateContextAsync();
        if (!(await AgentFeatureSettings.FeaturesAsync(db, ct)).HelpDeskEnabled) return new("Help desk is disabled by your administrator.");
        var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == deviceId && x.ArchivedAtUtc == null &&
            x.ProvisionStatus != DeviceProvisionStatus.Archived, ct) ?? throw new UnauthorizedAccessException();
        var (keys, name) = await Identity(db, sid, ct);
        var owned = Owned(db, keys);
        if (request.Operation == HelpOperation.Count)
            return new() { Identity = keys[0], UnreadCount = await owned.CountAsync(x => x.LastStaffMessageId > x.ReadThroughMessageId, ct),
                LatestUpdate = await owned.Where(x => x.LastStaffMessageId > x.ReadThroughMessageId).Select(x => (long?)x.LastStaffMessageId).MaxAsync(ct) ?? 0 };
        if (request.Operation == HelpOperation.List)
        {
            var canCreate = await HelpDeskScope.CanCreateAsync(db, deviceId, sid, ct);
            if (request.StatusFilter is { } status) owned = owned.Where(x => x.Status == status);
            var rows = await owned.OrderByDescending(x => x.UpdatedAtUtc).ThenByDescending(x => x.Id).Skip(request.Offset).Take(51).ToArrayAsync(ct);
            return new() { Identity = keys[0], Tickets = await Summaries(db, rows.Take(50), ct), HasMore = rows.Length > 50,
                CanCreate = canCreate, ScopeMessage = canCreate ? null : HelpDeskScope.Denied };
        }
        if (request.Operation is HelpOperation.Read or HelpOperation.Screenshot)
        {
            var ticket = await owned.SingleOrDefaultAsync(x => x.Id == request.TicketId, ct) ?? throw new UnauthorizedAccessException();
            if (request.Operation == HelpOperation.Screenshot)
                return new() { Screenshot = await db.HelpTicketMessages.Where(x => x.TicketId == ticket.Id && x.Id == request.MessageId)
                    .Select(x => x.Screenshot).SingleOrDefaultAsync(ct) };
            return await Conversation(db, ticket, request.MessageId, ct);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(682947240, 1)", ct);
        if (request.Operation == HelpOperation.MarkRead)
        {
            var ticket = await owned.SingleOrDefaultAsync(x => x.Id == request.TicketId, ct) ?? throw new UnauthorizedAccessException();
            if (await db.HelpTicketMessages.AnyAsync(x => x.TicketId == ticket.Id && x.Id == request.MessageId, ct))
            {
                if (request.MessageId > ticket.ReadThroughMessageId)
                {
                    ticket.ReadThroughMessageId = request.MessageId;
                    await NotifyAsync(db, ticket.Id, ct);
                }
            }
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new();
        }
        if (request.Operation == HelpOperation.Reopen)
        {
            if (request.CommandId == Guid.Empty) throw new InvalidOperationException(UiText.Get(LocKeys.Ui.HelpDeskService.InvalidReopenRequest));
            var ticket = await owned.SingleOrDefaultAsync(x => x.Id == request.TicketId, ct) ?? throw new UnauthorizedAccessException();
            if (await db.HelpTicketMessages.AnyAsync(x => x.TicketId == ticket.Id && x.CommandId == request.CommandId, ct) ||
                ticket.Status != HelpTicketStatus.Closed) return await Conversation(db, ticket, 0, ct);
            db.Add(new HelpTicketStatusEvent { TicketId = ticket.Id, Status = HelpTicketStatus.New, OccurredAtUtc = clock.GetUtcNow().UtcDateTime });
            ticket.Status = HelpTicketStatus.New;
            ticket.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
            db.Add(new HelpTicketMessage { TicketId = ticket.Id, CommandId = request.CommandId, Author = name,
                Body = "Request reopened.", CreatedAtUtc = ticket.UpdatedAtUtc });
            await db.SaveChangesAsync(ct);
            await HelpEmailNotifications.QueueAsync(db, ticket, "Ticket reopened", clock.GetUtcNow().UtcDateTime, ct);
            await NotifyAsync(db, ticket.Id, ct);
            await transaction.CommitAsync(ct);
            return await Conversation(db, ticket, 0, ct);
        }
        ValidateMessage(request.Body, request.Screenshot, request.CommandId);
        var reopenedByReply = false;
        HelpTicket target;
        if (request.Operation == HelpOperation.Create)
        {
            if (!await HelpDeskScope.CanCreateAsync(db, deviceId, sid, ct)) return new(HelpDeskScope.Denied);
            if (string.IsNullOrWhiteSpace(request.Subject) || request.Subject.Trim().Length > 200)
                throw new InvalidOperationException(UiText.Get(LocKeys.Ui.HelpDeskService.EnterASubjectOfUpTo200Characters));
            var duplicate = await owned.SingleOrDefaultAsync(x => x.CommandId == request.CommandId, ct);
            if (duplicate is not null) return await Conversation(db, duplicate, 0, ct);
            if (await owned.CountAsync(x => x.CreatedAtUtc > clock.GetUtcNow().UtcDateTime.AddHours(-1), ct) >= 10)
                throw new InvalidOperationException(UiText.Get(LocKeys.Ui.HelpDeskService.TooManyNewRequestsPleaseWaitBeforeSubmittingAnother));
            target = new() { CommandId = request.CommandId, RequesterKeys = keys, RequesterName = name,
                Subject = request.Subject.Trim(), DeviceId = deviceId, DeviceName = device.Name,
                CreatedAtUtc = clock.GetUtcNow().UtcDateTime, UpdatedAtUtc = clock.GetUtcNow().UtcDateTime };
            var rules = await db.Set<HelpRoutingRule>().AsNoTracking().ToArrayAsync(ct);
            var ips = await db.DeviceNics.Where(x => x.DeviceId == deviceId).Select(x => x.Ips).ToArrayAsync(ct);
            var addresses = ips.Where(x => x != null).SelectMany(x => x!.Split([',', ';', ' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).ToArray();
            var routingInput = new HelpRoutingInput(target.Subject, request.Body!, device.Name, addresses);
            if (rules.Any(r => r.Enabled && HelpRoutingEngine.Conditions(r).Any(c => HelpRoutingEngine.IsDirectory(c.Field))))
            {
                var (_, directoryUsers) = await DirectoryIdentity.UsersAsync(db, sid, ct);
                var directoryDevices = await HelpRoutingDirectory.Active(db).Where(x => x.Kind == DirectoryObjectKind.Computer &&
                    x.DirectorySourceId == device.DirectorySourceId && x.ObjectGuid == device.DirectoryObjectGuid).ToArrayAsync(ct);
                routingInput = await HelpRoutingDirectory.PopulateAsync(db, routingInput, directoryUsers, directoryDevices, ct);
            }
            var routing = HelpRoutingEngine.Evaluate(rules, routingInput);
            target.CategoryId = routing.CategoryId; target.QueueId = routing.QueueId; target.RoutingSummary = routing.Summary;
            target.AssignedToId = await SoleQueueMemberAsync(db, target.QueueId, ct);
            if (target.AssignedToId.HasValue) target.RoutingSummary += " Assigned to the queue's only member.";
            db.Add(target);
            await db.SaveChangesAsync(ct);
        }
        else
        {
            target = await owned.SingleOrDefaultAsync(x => x.Id == request.TicketId, ct) ?? throw new UnauthorizedAccessException();
            if (await db.HelpTicketMessages.AnyAsync(x => x.TicketId == target.Id && x.CommandId == request.CommandId, ct))
                return await Conversation(db, target, 0, ct);
            if (await db.HelpTicketMessages.CountAsync(x => x.TicketId == target.Id && !x.FromStaff &&
                    x.CreatedAtUtc > clock.GetUtcNow().UtcDateTime.AddMinutes(-1), ct) >= 10)
                throw new InvalidOperationException(UiText.Get(LocKeys.Ui.HelpDeskService.PleaseWaitBeforeSendingAnotherReply));
            reopenedByReply = target.Status == HelpTicketStatus.Closed;
            if (target.Status is HelpTicketStatus.Closed or HelpTicketStatus.WaitingOnUser)
            {
                db.Add(new HelpTicketStatusEvent { TicketId = target.Id, Status = HelpTicketStatus.New, OccurredAtUtc = clock.GetUtcNow().UtcDateTime });
                target.Status = HelpTicketStatus.New;
            }
        }
        target.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        db.Add(new HelpTicketMessage { TicketId = target.Id, CommandId = request.CommandId, Author = name,
            Body = request.Body!.Trim(), CreatedAtUtc = target.UpdatedAtUtc, Screenshot = request.Screenshot });
        await db.SaveChangesAsync(ct);
        await HelpEmailNotifications.QueueAsync(db, target, request.Operation == HelpOperation.Create ? "New ticket" : reopenedByReply ? "Ticket reopened" : null, clock.GetUtcNow().UtcDateTime, ct);
        await NotifyAsync(db, target.Id, ct);
        await transaction.CommitAsync(ct);
        return await Conversation(db, target, 0, ct);
    }

    public async Task<HelpResponse> ListAsync(ClaimsPrincipal user, string? search, HelpTicketStatus? status, int? assignedTo, int offset, CancellationToken ct = default, bool openOnly = false)
    {
        return await ListRoutedAsync(user, search, status, assignedTo, offset, openOnly, null, null, false, ct);
    }
    public Task<HelpResponse> ListRoutedAsync(ClaimsPrincipal user, string? search, HelpTicketStatus? status, int? assignedTo,
        int offset, bool openOnly, int? queueId, int? categoryId, bool myQueues, CancellationToken ct = default) =>
        ListFilteredAsync(user, search, new(queueId.HasValue ? [queueId.Value] : null,
            categoryId.HasValue ? [categoryId.Value] : null, status.HasValue ? [status.Value] : null),
            assignedTo, offset, openOnly, myQueues, ct);

    public async Task<HelpResponse> ListFilteredAsync(ClaimsPrincipal user, string? search, HelpTicketListFilter filter, int? assignedTo,
        int offset, bool openOnly, bool myQueues, CancellationToken ct = default)
    {
        var access = await AccessAsync(user);
        await using var db = await factory.CreateContextAsync();
        var query = Visible(db.HelpTickets.AsNoTracking(), access);
        query = filter.Apply(query);
        if (myQueues) query = query.Where(x => x.QueueId != HelpQueue.UnassignedId && x.Queue != null && x.Queue.Members.Any(m => m.UserId == access.UserId));
        if (openOnly) query = query.Where(x => x.Status != HelpTicketStatus.Closed);
        if (assignedTo.HasValue) query = assignedTo == 0 ? query.Where(x => x.AssignedToId == null) : query.Where(x => x.AssignedToId == assignedTo);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            var number = term.TrimStart('#');
            query = query.Where(x => x.Subject.ToLower().Contains(term) || x.RequesterName.ToLower().Contains(term) || x.DeviceName.ToLower().Contains(term) || x.Id.ToString() == number);
        }
        var rows = await query.OrderByDescending(x => x.UpdatedAtUtc).ThenByDescending(x => x.Id).Skip(Math.Clamp(offset, 0, 100000)).Take(51).ToArrayAsync(ct);
        return new() { Tickets = await Summaries(db, rows.Take(50), ct), HasMore = rows.Length > 50 };
    }
    public async Task<(int Total, HelpTicketSummary[] Tickets)> DirectoryHistoryAsync(ClaimsPrincipal user, int source, Guid guid, int page, int size, CancellationToken ct)
    {
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.AdminOnly)).Succeeded) throw new UnauthorizedAccessException();
        var access = await AccessAsync(user);
        if (page < 0 || size is < 1 or > 100 || (long)page * size > int.MaxValue) throw new InvalidOperationException(UiText.Get(LocKeys.Ui.ActiveDirectoryService.InvalidHistoryPage));
        await using var db = await factory.CreateContextAsync();
        var identity = await DirectoryHistoryIdentity.ReadAsync(db, source, guid, ct);
        var query = Visible(Owned(db, identity.HelpKeys).AsNoTracking(), access);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.UpdatedAtUtc).ThenByDescending(x => x.Id).Skip(page * size).Take(size).ToArrayAsync(ct);
        return (total, await Summaries(db, rows, ct));
    }
    public async Task<(int Total, HelpTicketSummary[] Tickets)> DeviceHistoryAsync(ClaimsPrincipal user, int deviceId, int page, int size, CancellationToken ct)
    {
        if (!(await authorization.AuthorizeAsync(user, Constants.Permissions.DeviceView)).Succeeded) throw new UnauthorizedAccessException();
        var access = await AccessAsync(user);
        if (page < 0 || size is < 1 or > 100 || (long)page * size > int.MaxValue) throw new InvalidOperationException(UiText.Get(LocKeys.Ui.ActiveDirectoryService.InvalidHistoryPage));
        await using var db = await factory.CreateContextAsync();
        var query = Visible(db.HelpTickets.AsNoTracking().Where(x => x.DeviceId == deviceId), access);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.UpdatedAtUtc).ThenByDescending(x => x.Id).Skip(page * size).Take(size).ToArrayAsync(ct);
        return (total, await Summaries(db, rows, ct));
    }
    public async Task<HelpResponse> ReadAsync(ClaimsPrincipal user, int id, long before = 0, CancellationToken ct = default)
    {
        var access = await AccessAsync(user);
        await using var db = await factory.CreateContextAsync();
        var ticket = await Visible(db.HelpTickets.AsNoTracking(), access).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new InvalidOperationException(UiText.Get(LocKeys.Ui.HelpDeskService.TicketNotFound));
        return await Conversation(db, ticket, before, ct);
    }
    public async Task<byte[]?> ScreenshotAsync(ClaimsPrincipal user, int id, long messageId, CancellationToken ct = default)
    {
        var access = await AccessAsync(user);
        await using var db = await factory.CreateContextAsync();
        if (!await Visible(db.HelpTickets.AsNoTracking(), access).AnyAsync(x => x.Id == id, ct)) throw new UnauthorizedAccessException();
        return await db.HelpTicketMessages.Where(x => x.TicketId == id && x.Id == messageId).Select(x => x.Screenshot).SingleOrDefaultAsync(ct);
    }
    public async Task<HelpRoutingSettings> RoutingOptionsAsync(ClaimsPrincipal user)
    {
        var access = await AccessAsync(user);
        await using var db = await factory.CreateContextAsync();
        return new(await db.Set<HelpCategory>().AsNoTracking().OrderBy(x => x.Name).ToArrayAsync(),
            await db.Set<HelpQueue>().AsNoTracking().Include(x => x.Members)
                .OrderBy(x => x.Name).ToArrayAsync(), []);
    }
    public async Task<HelpStaff[]> StaffAsync(ClaimsPrincipal user)
    {
        var access = await AccessAsync(user);
        await using var db = await factory.CreateContextAsync();
        var result = new List<HelpStaff>();
        foreach (var candidate in await db.Users.AsNoTracking().OrderBy(x => x.UserName).ToListAsync())
            if (candidate.LockoutEnd is null || candidate.LockoutEnd <= clock.GetUtcNow())
            {
                var candidateAccess = await RecipientAccessAsync(await principals.CreateAsync(candidate));
                if (!candidateAccess.ViewAll && !candidateAccess.ViewAssigned && !candidateAccess.ViewUnassigned)
                    continue;
                result.Add(new(candidate.Id, candidate.UserName ?? candidate.Id.ToString())
                {
                    ManageAssigned = candidateAccess.ViewAssigned, ManageAll = candidateAccess.ViewAll,
                    ManageUnassigned = candidateAccess.ViewUnassigned
                });
            }
        return result.ToArray();
    }
    private async Task<int?> SoleQueueMemberAsync(CloneDeployDbContext db, int? queueId, CancellationToken ct = default)
    {
        if (queueId is null or HelpQueue.UnassignedId) return null;
        var members = await db.Set<HelpQueueMember>().Where(x => x.QueueId == queueId).Select(x => x.UserId).Take(2).ToArrayAsync(ct);
        if (members.Length != 1) return null;
        var candidate = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == members[0], ct);
        if (candidate is null || candidate.LockoutEnd > clock.GetUtcNow()) return null;
        return await CanReceiveAsync(await principals.CreateAsync(candidate)) ? candidate.Id : null;
    }

    private async Task<Access> RecipientAccessAsync(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true ||
            !int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id)) throw new UnauthorizedAccessException();
        // Recipients have no browser session. Evaluate current application/group permissions,
        // not session-specific MFA enrollment policies. PermissionHandler checks application access.
        async Task<bool> Has(string permission) => (await authorization.AuthorizeAsync(user, resource: null,
            requirements: new[] { new PermissionRequirement(permission) })).Succeeded;
        return new Access(id, await Has(Constants.Permissions.HelpDeskViewAll),
            await Has(Constants.Permissions.HelpDeskViewAssigned), await Has(Constants.Permissions.HelpDeskViewUnassigned), true);
    }

    private async Task<bool> CanReceiveAsync(ClaimsPrincipal user)
    {
        var access = await RecipientAccessAsync(user);
        return access.ViewAll || access.ViewAssigned || access.ViewUnassigned;
    }
    public async Task UpdateAsync(ClaimsPrincipal user, int id, HelpTicketStatus status, int? assignedTo, string? reply, Guid commandId, DateTime? expectedUpdatedAtUtc = null)
        => await UpdateTicketAsync(user, id, status, assignedTo, reply, commandId, expectedUpdatedAtUtc, null);

    public sealed record RoutingChange(int? CategoryId, int? QueueId);

    public async Task UpdateTicketAsync(ClaimsPrincipal user, int id, HelpTicketStatus status, int? assignedTo, string? reply,
        Guid commandId, DateTime? expectedUpdatedAtUtc, RoutingChange? routing)
    {
        var access = await AccessAsync(user);
        var staffId = access.UserId;
        if (!System.Enum.IsDefined(status) || commandId == Guid.Empty || reply?.Length > 4000) throw new InvalidOperationException(UiText.Get(LocKeys.Ui.HelpDeskService.InvalidTicketUpdate));
        await using var db = await factory.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(682947240, 1)");
        var ticket = await Visible(db.HelpTickets, access).SingleOrDefaultAsync(x => x.Id == id) ?? throw new InvalidOperationException(UiText.Get(LocKeys.Ui.HelpDeskService.TicketNotFound));
        // Visibility is checked inside the same transaction as the update so a reassignment cannot be bypassed.
        if (!access.Manage) throw new UnauthorizedAccessException();
        if (await db.HelpTicketMessages.AnyAsync(x => x.TicketId == id && x.CommandId == commandId)) return;
        if (assignedTo.HasValue && ticket.AssignedToId != assignedTo && !(await StaffAsync(user)).Any(x => x.Id == assignedTo))
            throw new InvalidOperationException(UiText.Get(LocKeys.Ui.HelpDeskService.SelectAStaffMemberWhoCanManageAndView));
        if (expectedUpdatedAtUtc.HasValue && ticket.UpdatedAtUtc != expectedUpdatedAtUtc)
            throw new InvalidOperationException(UiText.Get(LocKeys.Ui.HelpDeskService.ThisTicketHasChangedRefreshItBeforeSavingYour));
        if (routing is not null) routing = routing with { QueueId = routing.QueueId ?? HelpQueue.UnassignedId };
        var oldQueue = ticket.QueueId;
        var oldStatus = ticket.Status;
        var changes = new List<string>();
        if (routing is not null)
        {
            if (routing.CategoryId is {} category && !await db.Set<HelpCategory>().AnyAsync(x => x.Id == category)) throw new InvalidOperationException(UiText.Get(LocKeys.Ui.HelpDeskService.CategoryNoLongerExists));
            if (routing.QueueId is {} queue)
            {
                if (!await db.Set<HelpQueue>().AnyAsync(x => x.Id == queue))
                    throw new InvalidOperationException(UiText.Get(LocKeys.Ui.HelpDeskService.QueueNoLongerExists));
                if (queue != HelpQueue.UnassignedId && assignedTo is {} owner && (ticket.QueueId != routing.QueueId || ticket.AssignedToId != assignedTo) && !await db.Set<HelpQueueMember>().AnyAsync(x => x.QueueId == queue && x.UserId == owner))
                    throw new InvalidOperationException(UiText.Get(LocKeys.Ui.HelpDeskService.TheAssignedStaffMemberMustBelongToTheSelected));
            }
            if (ticket.CategoryId != routing.CategoryId) changes.Add("Category changed.");
            if (ticket.QueueId != routing.QueueId)
            {
                changes.Add("Queue changed.");
                if (assignedTo is null)
                    assignedTo = await SoleQueueMemberAsync(db, routing.QueueId);
            }
            ticket.CategoryId = routing.CategoryId; ticket.QueueId = routing.QueueId;
        }
        else if (ticket.QueueId is {} existingQueue && existingQueue != HelpQueue.UnassignedId && assignedTo is {} existingOwner && ticket.AssignedToId != assignedTo &&
            !await db.Set<HelpQueueMember>().AnyAsync(x => x.QueueId == existingQueue && x.UserId == existingOwner))
            throw new InvalidOperationException(UiText.Get(LocKeys.Ui.HelpDeskService.TheAssignedStaffMemberMustBelongToTheTicket));
        if (assignedTo is { } selectedOwner && (ticket.AssignedToId != assignedTo || oldQueue != ticket.QueueId))
        {
            var account = await db.Users.SingleAsync(x => x.Id == selectedOwner);
            var recipient = await RecipientAccessAsync(await principals.CreateAsync(account));
            if (!recipient.ViewAll && !recipient.ViewAssigned && !(recipient.ViewUnassigned && ticket.QueueId == HelpQueue.UnassignedId))
                throw new InvalidOperationException("The selected application user cannot manage tickets in this queue.");
        }
        if (ticket.Status != status) changes.Add($"Status changed to {StatusLabel(status)}.");
        if (ticket.AssignedToId != assignedTo) changes.Add(assignedTo.HasValue ? "Assigned to a help desk staff member." : "Assignment cleared.");
        if (string.IsNullOrWhiteSpace(reply) && changes.Count == 0) return;
        var body = string.IsNullOrWhiteSpace(reply) ? string.Join(" ", changes) : reply.Trim() +
            (changes.Count > 0 ? "\n\n" + string.Join(" ", changes) : "");
        if (body.Length > 4000) throw new InvalidOperationException(UiText.Get(LocKeys.Ui.HelpDeskService.TheReplyAndStatusUpdateMustFitWithin4));
        if (ticket.Status != status)
            db.Add(new HelpTicketStatusEvent { TicketId = ticket.Id, Status = status, OccurredAtUtc = clock.GetUtcNow().UtcDateTime });
        ticket.Status = status;
        ticket.AssignedToId = assignedTo;
        ticket.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        var message = new HelpTicketMessage { TicketId = id, CommandId = commandId, Author = user.Identity?.Name ?? "Help Desk",
            StaffUserId = staffId, FromStaff = true, Body = body, CreatedAtUtc = ticket.UpdatedAtUtc };
        db.Add(message);
        await db.SaveChangesAsync();
        ticket.LastStaffMessageId = message.Id;
        db.AuditLogs.Add(new AuditLog { AppUserId = staffId, UserName = message.Author, AuditType = AuditType.Update,
            ObjectType = "HelpTicket", ObjectId = id, ObjectName = ticket.Subject, LoggedAtUtc = ticket.UpdatedAtUtc,
            ObjectJson = JsonSerializer.Serialize(new { status, assignedTo, ticket.CategoryId, ticket.QueueId, MessageId = message.Id }) });
        await db.SaveChangesAsync();
        if (oldQueue != ticket.QueueId || oldStatus == HelpTicketStatus.Closed && status != HelpTicketStatus.Closed)
            await HelpEmailNotifications.QueueAsync(db, ticket, oldQueue != ticket.QueueId ? "Ticket transferred" : "Ticket reopened", clock.GetUtcNow().UtcDateTime, CancellationToken.None);
        await NotifyAsync(db, ticket.Id, CancellationToken.None);
        await transaction.CommitAsync();
    }
    internal async Task<string[]> EmailRecipientsAsync(int ticketId, CancellationToken ct)
    {
        await using var db = await factory.CreateContextAsync();
        if (!(await AgentFeatureSettings.FeaturesAsync(db, ct)).HelpDeskEnabled) return [];
        var ticket = await db.HelpTickets.AsNoTracking().Include(x => x.Queue).ThenInclude(x => x!.Members).SingleOrDefaultAsync(x => x.Id == ticketId, ct);
        if (ticket is null || ticket.Status == HelpTicketStatus.Closed || ticket.Queue is { EmailEnabled: false }) return [];
        var ids = ticket.AssignedToId is { } owner ? new[] { owner } : ticket.Queue?.Members.Select(x => x.UserId).ToArray() ?? [];
        var candidates = await db.Users.AsNoTracking().Where(x => ids.Contains(x.Id)).ToArrayAsync(ct);
        var recipients = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (candidate.LockoutEnd > clock.GetUtcNow() || string.IsNullOrWhiteSpace(candidate.Email) || !SmtpEmailTransport.ValidAddress(candidate.Email.Trim())) continue;
            var principal = await principals.CreateAsync(candidate);
            if (!await CanReceiveAsync(principal)) continue;
            var access = await RecipientAccessAsync(principal);
            if (await Visible(db.HelpTickets, access).AnyAsync(x => x.Id == ticketId, ct)) recipients.Add(candidate.Email.Trim());
        }
        return recipients.ToArray();
    }
    private static Task NotifyAsync(CloneDeployDbContext db, int ticketId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_notify('help_ticket_changed', current_schema() || ':' || {ticketId}::text)", ct);

    public static string StatusLabel(HelpTicketStatus status) => status switch
    {
        HelpTicketStatus.InProgress => "In progress", HelpTicketStatus.WaitingOnUser => "Waiting on user",
        HelpTicketStatus.Closed => "Closed", _ => "New"
    };
    private static void ValidateMessage(string? body, byte[]? screenshot, Guid commandId)
    {
        if (commandId == Guid.Empty || string.IsNullOrWhiteSpace(body) || body.Trim().Length > 4000)
            throw new InvalidOperationException(UiText.Get(LocKeys.Ui.HelpDeskService.EnterADescriptionOfUpTo4000Characters));
        if (screenshot is null) return;
        if (screenshot.Length < 33 || screenshot.Length > ScreenshotLimit ||
            !screenshot.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            !screenshot.AsSpan(12, 4).SequenceEqual("IHDR"u8) ||
            BinaryPrimitives.ReadUInt32BigEndian(screenshot.AsSpan(16, 4)) is 0 or > HelpAttachmentLimits.MaximumDimension ||
            BinaryPrimitives.ReadUInt32BigEndian(screenshot.AsSpan(20, 4)) is 0 or > HelpAttachmentLimits.MaximumDimension)
            throw new InvalidOperationException(UiText.Get(LocKeys.Ui.HelpDeskService.TheScreenshotMustBeAPNGOfUpTo));
    }
    private static async Task<HelpTicketSummary[]> Summaries(CloneDeployDbContext db, IEnumerable<HelpTicket> tickets, CancellationToken ct)
    {
        var rows = tickets.ToArray();
        var ids = rows.Where(x => x.AssignedToId.HasValue).Select(x => x.AssignedToId!.Value).Distinct().ToArray();
        var names = await db.Users.Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.UserName, ct);
        var categories = await db.Set<HelpCategory>().AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var queues = await db.Set<HelpQueue>().AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        return rows.Select(x => new HelpTicketSummary(x.Id, x.Subject, x.Status, x.UpdatedAtUtc, x.RequesterName, x.DeviceId,
            x.DeviceName, x.AssignedToId.HasValue ? names.GetValueOrDefault(x.AssignedToId.Value) : null,
            x.LastStaffMessageId > x.ReadThroughMessageId, x.AssignedToId)
            { CategoryId = x.CategoryId, Category = x.CategoryId is {} category ? categories.GetValueOrDefault(category) : null,
              QueueId = x.QueueId, Queue = x.QueueId is {} queue ? queues.GetValueOrDefault(queue) : null, RoutingSummary = x.RoutingSummary }).ToArray();
    }
    private static async Task<HelpResponse> Conversation(CloneDeployDbContext db, HelpTicket ticket, long before, CancellationToken ct)
    {
        var messages = await db.HelpTicketMessages.AsNoTracking().Where(x => x.TicketId == ticket.Id && (before == 0 || x.Id < before))
            .OrderByDescending(x => x.Id).Take(26).Select(x => new HelpMessage(x.Id, x.Author, x.Body, x.CreatedAtUtc, x.FromStaff, x.Screenshot != null)).ToArrayAsync(ct);
        return new() { Ticket = (await Summaries(db, [ticket], ct))[0], Messages = messages.Take(25).Reverse().ToArray(), HasMore = messages.Length > 25 };
    }
}
