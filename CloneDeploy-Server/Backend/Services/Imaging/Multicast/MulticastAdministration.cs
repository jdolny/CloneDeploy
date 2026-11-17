using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using CloneDeploy.SiteServices;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed record MulticastCreate(
    string Name,
    int ImageId,
    int ProfileId,
    int ProviderId,
    int Receivers = 1,
    int MegabitsPerSecond = 100,
    int Ttl = 1,
    int? GroupId = null);
public sealed record MulticastProvider(int Id, string Name, string Host);
public sealed record MulticastRow(Guid Id, string Name, string Image, string Host, MulticastState State, int Expected, int Joined, string? Error)
{
    public string RequestedBy { get; init; } = "System / unknown";
    public bool CanManage { get; init; }
    public bool CanRemoveHistory { get; init; }
}

public sealed partial class ImagingDeploymentService
{
    public async Task<List<MulticastProvider>> MulticastProvidersAsync(int imageId)
    {
        if (await GetGroupDeploymentUserAsync(Constants.Permissions.DeviceGroupDeployMulticast) is null) throw new UnauthorizedAccessException();
        await using var db = await factory.CreateContextAsync();
        var version = await db.ImageVersions.SingleOrDefaultAsync(x => x.ImageId == imageId && x.IsActive);
        if (version is null || version.Format != ImageFormat.Wim || version.Pipable != true) return [];
        return await MulticastQueue.Providers(db, version).Select(x => new MulticastProvider(x.Id, x.Name,
            x.SiteServiceId == null ? "Main" : x.SiteService!.Name)).ToListAsync();
    }

    public Task<List<MulticastRow>> MulticastListAsync() => MulticastListAsync(false);

    public async Task<List<MulticastRow>> MulticastListAsync(bool allTasks)
    {
        var access = await TaskAccess.ReadAsync(authentication, authorization) ?? throw new UnauthorizedAccessException();
        await using var db = await factory.CreateContextAsync();
        if (allTasks && !access.ViewAll) throw new UnauthorizedAccessException();
        return await db.Set<MulticastSession>().Where(x => allTasks || x.CreatedBy == access.UserId).OrderByDescending(x => x.CreatedAtUtc).Take(100)
            .Select(x => new MulticastRow(x.Id, x.Name, db.ImageVersions.Where(v => v.Id == x.ImageVersionId).Select(v => v.Name).First(),
                x.SiteServiceId == null ? "Main" : db.SiteServices.Where(s => s.Id == x.SiteServiceId).Select(s => s.Name).First(),
                x.State, x.ExpectedReceivers, db.Set<ImagingDeployment>().Count(d => d.MulticastSessionId == x.Id && d.ImagerStartedAtUtc != null), x.Error)
                { CanManage = access.ManageAll || x.CreatedBy == access.UserId, CanRemoveHistory = access.ManageAll,
                  RequestedBy = db.Users.Where(u => u.Id == x.CreatedBy).Select(u => u.UserName).FirstOrDefault() ?? "System / unknown" }).ToListAsync();
    }

    public async Task<Guid> CreateMulticastAsync(MulticastCreate request, DeviceGroupService groups)
    {
        var user = await GetGroupDeploymentUserAsync(Constants.Permissions.DeviceGroupDeployMulticast) ?? throw new UnauthorizedAccessException();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 100 || request.Receivers is < 1 or > 1000 ||
            request.MegabitsPerSecond is < 1 or > 100000 || request.Ttl is < 1 or > 32)
            throw new InvalidOperationException("Enter a session name, 1–1000 receivers, a 1–100000 Mbit/s rate, and TTL 1–32.");
        int[] members = [];
        if (request.GroupId is { } groupId)
        {
            var group = await groups.GetByIdAsync(groupId);
            var devices = await groups.GetMembersAsync(groupId);
            if (!group.HasData || !devices.HasData || group.Data.ImageId != request.ImageId || group.Data.DeploymentProfileId != request.ProfileId)
                throw new InvalidOperationException("The group's image or deployment profile changed. Reload the review.");
            members = devices.Data.Select(x => x.Id).Distinct().ToArray();
            if (members.Length is < 1 or > 1000) throw new InvalidOperationException("Multicast groups require 1–1000 devices.");
        }
        var session = new MulticastSession { Id = Guid.NewGuid(), Name = request.Name.Trim(), DeploymentProfileId = request.ProfileId,
            StorageProviderId = request.ProviderId, ExpectedReceivers = request.GroupId == null ? request.Receivers : members.Length,
            MegabitsPerSecond = request.MegabitsPerSecond, Ttl = request.Ttl, GroupId = request.GroupId, CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!), State = MulticastState.Preparing };
        await using (var db = await factory.CreateContextAsync())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(682947235, 0)");
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(903419)");
            var version = await db.ImageVersions.SingleOrDefaultAsync(x => x.ImageId == request.ImageId && x.IsActive && x.Image!.Enabled)
                ?? throw new InvalidOperationException("The image has no active version.");
            if (!version.IsApproved && !(await authorization.AuthorizeAsync(user, Constants.Permissions.ImageDeployUnapproved)).Succeeded)
                throw new UnauthorizedAccessException("Deploy unapproved version permission is required.");
            if (version.Format != ImageFormat.Wim || version.Pipable != true || version.Layout != "WindowsPartition" || version.FileName != "image.wim" ||
                version.SizeBytes <= 0 || version.Sha256 is not { Length: 64 } || !version.Sha256.All(Uri.IsHexDigit))
                throw new InvalidOperationException("Multicast requires a verified pipable WIM captured by Imager. FFU is not supported.");
            if (!await db.ImageProfiles.AnyAsync(x => x.Id == request.ProfileId && x.Enabled && (request.GroupId != null || x.VisibleInWinPe)))
                throw new InvalidOperationException("Select an enabled deployment profile (visible in WinPE for manual sessions).");
            var provider = await MulticastQueue.Providers(db, version).SingleOrDefaultAsync(x => x.Id == request.ProviderId)
                ?? throw new InvalidOperationException("Select an enabled storage provider with a ready copy of this image.");
            session.ImageVersionId = version.Id;
            session.SiteServiceId = provider.SiteServiceId;
            if (provider.SiteServiceId is { } capabilitySite)
            {
                var capabilities = await SiteServiceAdministration.ReadProxyAccessAsync(db, capabilitySite);
                if (!capabilities.MulticastEnabled || !capabilities.StorageEnabled) throw new InvalidOperationException("Storage and multicast must be enabled on the selected site.");
            }
            var address = provider.SiteServiceId is { } siteId
                ? await db.SiteServices.Where(x => x.Id == siteId).Select(x => x.ListenAddress).SingleAsync()
                : await db.AppSettings.Where(x => x.Name == ServerSettingsService.MulticastSenderAddressKey).Select(x => x.Value).SingleOrDefaultAsync();
            if (!provider.SiteServiceId.HasValue && string.IsNullOrWhiteSpace(address))
                address = await db.AppSettings.Where(x => x.Name == ServerUrlSettingsStore.MainServerAddressKey)
                    .Select(x => x.Value).SingleOrDefaultAsync();
            if (!provider.SiteServiceId.HasValue &&
                (!System.Net.IPAddress.TryParse(address?.Trim(), out var senderIp) ||
                 senderIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
                 senderIp.GetAddressBytes()[0] is 0 or >= 224))
                throw new InvalidOperationException("Configure a unicast IPv4 Main Server IP in Admin Settings > Server Settings, or set a multicast sender IPv4 override in Admin Settings > Imaging > Multicast. An IPv6 Main Server IP requires an IPv4 override.");
            if (string.IsNullOrWhiteSpace(address))
                throw new InvalidOperationException(provider.SiteServiceId.HasValue
                    ? "Configure the selected Site Service's listening IPv4 address before creating a multicast session."
                    : "Configure the main app's multicast sender IPv4 address in Admin Settings > Imaging > Multicast before creating a session.");
            session.SenderAddress = address.Trim();
            // Let cancelled receivers and sender leases drain before reusing a UDP rendezvous.
            var quarantine = DateTime.UtcNow.AddMinutes(-5);
            var used = await db.Set<MulticastSession>().Where(x => x.State < MulticastState.Completed || x.FinishedAtUtc > quarantine).Select(x => x.Port).ToListAsync();
            session.Port = Enumerable.Range(0, 1000).Select(n => 39000 + n * 2).FirstOrDefault(p => !used.Contains(p));
            if (session.Port == 0) throw new InvalidOperationException("No multicast ports are available.");
            session.GroupAddress = $"239.192.{(session.Port - 39000) / 256}.{(session.Port - 39000) % 256 + 1}";
            UdpcastSender.Validate(new(session.Id, session.SenderAddress, session.GroupAddress, session.Port));
            db.Add(session);
            db.Add(new StorageReadLease { Id = session.Id, StorageProviderId = provider.Id, ContentKey = "image:" + version.Id.ToString("N"), ExpiresAtUtc = DateTime.UtcNow.AddHours(13) });
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        try
        {
            foreach (var device in members)
            {
                var result = await PrepareCoreAsync(device, request.ImageId, request.ProfileId, user, null, false, null, session.Id, null);
                if (!result.Success) throw new InvalidOperationException($"Device {device}: {result.Error!.Args.FirstOrDefault() ?? result.Error.Code}");
            }
            await using var db = await factory.CreateContextAsync();
            await db.Set<MulticastSession>().Where(x => x.Id == session.Id && x.State == MulticastState.Preparing)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, MulticastState.Waiting));
            return session.Id;
        }
        catch
        {
            await using var cleanup = await factory.CreateContextAsync();
            await MulticastQueue.CancelAsync(cleanup, session.Id, CancellationToken.None);
            throw;
        }
    }

    public async Task CancelMulticastAsync(Guid id)
    {
        var access = await TaskAccess.ReadAsync(authentication, authorization) ?? throw new UnauthorizedAccessException();
        await using var db = await factory.CreateContextAsync();
        var owner = await db.Set<MulticastSession>().Where(x => x.Id == id).Select(x => (int?)x.CreatedBy).SingleOrDefaultAsync();
        if (!access.CanManage(owner)) throw new UnauthorizedAccessException();
        await MulticastQueue.CancelAsync(db, id, CancellationToken.None);
    }

    public async Task RemoveMulticastHistoryAsync(Guid id)
    {
        var access = await TaskAccess.ReadAsync(authentication, authorization) ?? throw new UnauthorizedAccessException();
        if (!access.ManageAll) throw new UnauthorizedAccessException();
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(682947235, 0)");
        var session = await db.Set<MulticastSession>().SingleOrDefaultAsync(x => x.Id == id);
        if (session is null) return;
        if (!access.CanManage(session.CreatedBy)) throw new UnauthorizedAccessException();
        if (session.State < MulticastState.Completed || session.FinishedAtUtc > DateTime.UtcNow.AddMinutes(-5))
            throw new InvalidOperationException("Wait five minutes after the sender stops before removing its session history.");
        // Device deployment history and image references remain intact.
        await db.Set<ImagingDeployment>().Where(x => x.MulticastSessionId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.MulticastSessionId, (Guid?)null));
        await db.Set<StorageReadLease>().Where(x => x.Id == id).ExecuteDeleteAsync();
        db.Remove(session); await db.SaveChangesAsync(); await tx.CommitAsync();
    }

    public async Task<ServiceResult<ImagingDeployment>> JoinMulticastAsync(ClaimsPrincipal user, int device, Guid id, CancellationToken ct)
    {
        if (user.FindFirstValue("imaging_kind") != "user" || !int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return ServiceResult<ImagingDeployment>.Fail("Image.Forbidden");
        await using var db = await factory.CreateContextAsync();
        foreach (var permission in new[] { Constants.Permissions.ImagerDeploy, Constants.Permissions.ImagerOverride,
                     Constants.Permissions.ImageView, Constants.Permissions.DeploymentProfileView })
            if (!await ImagingPermissions.HasAsync(db, userId, permission, ct))
                return ServiceResult<ImagingDeployment>.Fail("Image.Forbidden");
        var session = await db.Set<MulticastSession>().SingleOrDefaultAsync(x => x.Id == id && x.GroupId == null && x.State == MulticastState.Waiting && !x.StartRequested, ct);
        if (session is null) return ServiceResult<ImagingDeployment>.Fail("Image.Validation", UiText.Get(LocKeys.Ui.MulticastAdministration.ThisSessionIsNoLongerAcceptingDevices));
        var image = await db.ImageVersions.Where(x => x.Id == session.ImageVersionId).Select(x => x.ImageId).SingleAsync(ct);
        return await PrepareCoreAsync(device, image, session.DeploymentProfileId, user, null, false, null, id, null);
    }
}
