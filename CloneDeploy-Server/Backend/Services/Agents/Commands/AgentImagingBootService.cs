using System.Security.Claims;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Common;
using CloneDeploy_Server.Backend.Data;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.WinPe;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>Reviews assigned imaging settings and dispatches an explicitly confirmed connected-agent boot.</summary>
public sealed class AgentImagingBootService(ICloneDeployDbFactory factory, AuthenticationStateProvider authentication,
    IAuthorizationService authorization, AgentConnectionService connections, AgentServerInstance server,
    IHubContext<AgentHub> hub, WinPeArtifactStorage artifacts, ImagingDeploymentService deployments)
{
    private async Task AuthorizeAsync()
    {
        if (!(await authorization.AuthorizeAsync((await authentication.GetAuthenticationStateAsync()).User, Constants.Permissions.DeviceDeployImageThroughAgent)).Succeeded)
            throw new UnauthorizedAccessException("Device permission is required to deploy an image through the agent.");
    }
    public async Task<AgentImagingReview> ReviewAsync(int deviceId)
    {
        await AuthorizeAsync();
        await using var db = await factory.CreateContextAsync();
        var device = await db.Devices.Include(d => d.ComputerSystem).Include(d => d.Os).SingleOrDefaultAsync(d => d.Id == deviceId && d.ArchivedAtUtc == null)
            ?? throw new InvalidOperationException("Device is unavailable.");
        var effective = await ImagingInheritance.ResolveAsync(db, device);
        if (effective.Error is not null) throw new InvalidOperationException(effective.Error);
        if (effective.ImageId is null || effective.ProfileId is null)
            throw new InvalidOperationException("Assign both an image and a deployment profile to this device first.");
        var image = await db.Images.SingleOrDefaultAsync(i => i.Id == effective.ImageId && i.Enabled)
            ?? throw new InvalidOperationException("The assigned image is missing or disabled.");
        var profile = await db.ImageProfiles.SingleOrDefaultAsync(p => p.Id == effective.ProfileId && p.Enabled)
            ?? throw new InvalidOperationException("The assigned deployment profile is missing or disabled.");
        var version = await db.ImageVersions.SingleOrDefaultAsync(v => v.ImageId == image.Id && v.IsActive)
            ?? throw new InvalidOperationException("The assigned image has no active version on enabled storage.");
        if (!await ImageReplicaCatalog.Providers(db, version).AnyAsync())
            throw new InvalidOperationException("No eligible storage provider holds the assigned image version.");
        var architecture = AgentWinPeSelection.RequireArchitecture(device.Os?.NativeArchitecture);
        var variants = await db.WinPeDefinitions.Include(v => v.PublishedBuild).Where(v => !v.IsBase).ToListAsync();
        var variant = AgentWinPeSelection.Resolve(variants, architecture, device.ComputerSystem?.Manufacturer, device.ComputerSystem?.Model);
        var build = variant.PublishedBuild ?? throw new InvalidOperationException("Build the matching or default WinPE boot environment first.");
        var request = JsonSerializer.Deserialize<BuildRequestDto>(build.RequestJson);
        var url = request?.Imager?.ServerUrl;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0)
            throw new InvalidOperationException("The current WinPE build has no HTTPS Imager server configured. Rebuild it.");
        var status = artifacts.Status(build);
        if (status.State != "Ready") throw new InvalidOperationException("The current WinPE build is not ready in storage.");
        var files = new[] { "boot.wim", "boot.sdi" }.Select(name =>
        {
            var source = architecture == "arm64" ? name.Replace("boot.", "boot-arm64.") : name;
            var file = status.Artifacts.SingleOrDefault(f => f.Name == source);
            if (file is null || file.Size <= 0 || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit))
                throw new InvalidOperationException("The current WinPE build is missing verified " + name + ". Rebuild it.");
            return new AgentBootFile(name, file.Size, file.Sha256);
        }).ToArray();
        // Resolve without defaults to distinguish an actual rule match from fallback.
        var defaultOnly = !WinPeModelMatching.MatchesModel(variant, device.ComputerSystem?.Manufacturer, device.ComputerSystem?.Model);
        return new(device.Id, device.Guid, device.Name, image.Id, version.Id, image.Name, profile.Id, profile.Name,
            build.Id, variant.Name, defaultOnly ? "Default WinPE boot environment" : "Device model match", uri.AbsoluteUri, files) { Architecture = architecture };
    }
    public async Task<AgentImagingReply> CheckAsync(AgentImagingReview review)
    {
        await AuthorizeAsync();
        var current = await ReviewAsync(review.DeviceId);
        if (JsonSerializer.Serialize(current) != JsonSerializer.Serialize(review)) throw new InvalidOperationException("Assignments or WinPE changed. Reload the review.");
        return await SendAsync(review, null);
    }
    public async Task<Guid> StartAsync(AgentImagingReview review)
    {
        var check = await CheckAsync(review);
        if (!check.Success) throw new InvalidOperationException(check.Detail);
        var prepared = await deployments.PrepareAgentBootAsync(review);
        if (!prepared.Success) throw new InvalidOperationException(string.Join(" ", prepared.Error.Args));
        var id = prepared.Data!.Id;
        AgentImagingReply reply;
        try { reply = await SendAsync(review, id); }
        catch
        {
            await deployments.CancelAsync(id, review.DeviceId);
            throw;
        }
        if (!reply.Success)
        {
            // The agent might have received a timed-out dispatch. Cancellation makes its next API check fail closed.
            await deployments.CancelAsync(id, review.DeviceId);
            throw new InvalidOperationException(reply.Detail);
        }
        return id;
    }
    private async Task<AgentImagingReply> SendAsync(AgentImagingReview review, Guid? deploymentId)
    {
        var connection = await connections.FindActiveAsync(review.DeviceId, default);
        if (connection is null || connection.ServerInstanceId != server.Id) throw new InvalidOperationException("Connect to the server instance where this agent is online.");
        var request = new AgentImagingCommand(Guid.NewGuid(), review.DeviceGuid, DateTimeOffset.UtcNow.AddSeconds(45), review.ServerUrl, deploymentId) { Architecture = review.Architecture };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        try
        {
            var reply = await hub.Clients.Client(connection.ConnectionId).InvokeAsync<AgentImagingReply>(AgentHubProtocol.ImagingBoot, request, timeout.Token);
            return reply.RequestId == request.RequestId && reply.Detail.Length <= 2000 ? reply : new(request.RequestId, false, "Invalid agent response.");
        }
        catch (Exception ex) when (ex is OperationCanceledException or HubException or IOException)
        { return new(request.RequestId, false, "The agent did not confirm the request. It may need an update or reconnection."); }
    }
}
