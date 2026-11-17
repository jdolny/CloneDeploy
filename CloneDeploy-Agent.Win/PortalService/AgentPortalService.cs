using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Runtime.InteropServices;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent;

/// <summary>Provides a bounded local portal and message acknowledgments without exposing the agent identity.</summary>
public sealed class AgentPortalService(
    AgentClient client,
    AgentOptions options,
    ILogger<AgentPortalService> logger,
    string pipeName = "CloneDeploy-Agent-Portal-v1") : BackgroundService
{
    private const int PipeBufferBytes = 4096;
    private const int MaximumConsentRequestBytes = 1024;
    private const int MaximumMessageSyncEntries = 1000;

    private const int MaximumResponseBytes = 1024 * 1024;
    private const int MaximumUsbRequestBytes = 8192;
    private const int MaximumDriverPackRequestBytes = 4096;
    private const int MaximumAcknowledgmentBytes = 256;
    private const int MaximumCachedIcons = 64;
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MetadataCacheLifetime = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ListenerRetryDelay = TimeSpan.FromSeconds(1);

    private enum PortalCommand : byte
    {
        Snapshot = PortalRequests.Snapshot,
        Notifications = PortalRequests.Notifications,
        PolicyIcon = PortalRequests.PolicyIcon,
        AdminSignIn = PortalRequests.AdminSignIn,
        AdminStatus = PortalRequests.AdminStatus,
        AdminSignOut = PortalRequests.AdminSignOut,
        StartDriverPack = PortalRequests.StartDriverPack,
        DriverPackStatus = PortalRequests.DriverPackStatus,
        CancelDriverPack = PortalRequests.CancelDriverPack,
        Usb = PortalRequests.Usb,
        AcknowledgeNotification = PortalRequests.AcknowledgeNotification,
        NotificationSnapshot = PortalRequests.NotificationSnapshot,
        ImagePrep = PortalRequests.ImagePrep,
        RemoteConsent = PortalRequests.RemoteConsent,
        Help = PortalRequests.Help,
        HelpPresence = PortalRequests.HelpPresence,
        WorkAccount = PortalRequests.WorkAccount,
    }

    private readonly Dictionary<string, DateTimeOffset> messageSyncTimes = [];
    private readonly Dictionary<string, byte[]> iconCache = [];
    private AgentPortalPolicy[]? cachedPolicies;
    private DateTimeOffset cacheExpires;
    private string? cachedSid;
    private AgentPortalIdentity? cachedDirectoryIdentity;
    private AgentPortalPolicy[] cachedAvailable = [];
    private DateTimeOffset identityCacheExpires;

    public AgentHelpPresence? HelpPresence { get; set; }

    public PortalNotificationJournal? Notifications { get; set; }

    public AgentAdminSession? Admin { get; set; }
    public AgentWorkAccount? WorkAccount { get; set; }

    public AgentDriverPackTool? DriverPacks { get; set; }

    public AgentUsbTool? Usb { get; set; }
    public AgentImagePrepTool? ImagePrep { get; set; }
    public AgentRemoteConsent? RemoteConsent { get; set; }
    internal Func<string, HelpRequest, CancellationToken, Task<HelpResponse>>? HelpRequestOverride { get; set; }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientSessionId(
        Microsoft.Win32.SafeHandles.SafePipeHandle pipe,
        out uint sessionId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        NamedPipeServerStream? pipe = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            var connected = false;

            try
            {
                if (pipe is null)
                {
                    var security = new PipeSecurity();
                    security.SetAccessRuleProtection(true, false);
                    security.AddAccessRule(new(
                        new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
                        PipeAccessRights.FullControl,
                        AccessControlType.Deny));
                    security.AddAccessRule(new(
                        new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                        PipeAccessRights.FullControl,
                        AccessControlType.Allow));
                    using var owner = WindowsIdentity.GetCurrent();
                    security.AddAccessRule(new(owner.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
                    // Do not grant CreateNewInstance to clients: they may only connect to our existing pipe.
                    security.AddAccessRule(new(
                        new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
                        PipeAccessRights.ReadWrite,
                        AccessControlType.Allow));
                    pipe = NamedPipeServerStreamAcl.Create(
                        pipeName,
                        PipeDirection.InOut,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
                        PipeBufferBytes,
                        PipeBufferBytes,
                        security);
                }

                await pipe.WaitForConnectionAsync(stoppingToken);
                connected = true;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(ConnectionTimeout);
                var request = new byte[1];
                await pipe.ReadExactlyAsync(request, timeout.Token);

                if (request[0] is < (byte)PortalCommand.Snapshot or > (byte)PortalCommand.WorkAccount)
                    continue;

                var command = (PortalCommand)request[0];
                // The display name is provided by Windows, never accepted from the UI.
                var userName = pipe.GetImpersonationUserName();

                if (string.IsNullOrWhiteSpace(userName))
                    throw new UnauthorizedAccessException();

                if (command == PortalCommand.WorkAccount)
                {
                    string? sid = null;
                    pipe.RunAsClient(() => { using var caller = WindowsIdentity.GetCurrent(true); sid = caller?.User?.Value; });
                    if (sid is null || WorkAccount is null) throw new UnauthorizedAccessException();
                    var workHeader = new byte[4]; await pipe.ReadExactlyAsync(workHeader, timeout.Token);
                    var length = BinaryPrimitives.ReadInt32LittleEndian(workHeader);
                    if (length is < 1 or > 8192) throw new IOException("Invalid work-account request size.");
                    var body = new byte[length]; await pipe.ReadExactlyAsync(body, timeout.Token);
                    var input = JsonSerializer.Deserialize<AgentWorkAccountRequest>(body) ?? throw new IOException("Invalid work-account request.");
                    identityCacheExpires = default; cachedSid = null;
                    var result = await WorkAccount.HandleAsync(sid, input, timeout.Token);
                    var workPayload = JsonSerializer.SerializeToUtf8Bytes(result);
                    BinaryPrimitives.WriteInt32LittleEndian(workHeader, workPayload.Length);
                    await pipe.WriteAsync(workHeader, timeout.Token); await pipe.WriteAsync(workPayload, timeout.Token);
                    _ = await pipe.ReadAsync(new byte[1], timeout.Token);
                    continue;
                }
                if (command == PortalCommand.HelpPresence)
                {
                    string? sid = null;
                    pipe.RunAsClient(() => { using var caller = WindowsIdentity.GetCurrent(true); sid = caller?.User?.Value; });
                    if (sid is null || !GetNamedPipeClientSessionId(pipe.SafePipeHandle, out var session) || session > int.MaxValue
                        || !GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var portalProcess) || portalProcess > int.MaxValue)
                        throw new UnauthorizedAccessException("Cannot identify the portal session.");
                    if (WorkAccount is not null) sid = (await WorkAccount.ResolveAsync(sid, timeout.Token)).Sid;
                    var presencePayload = JsonSerializer.SerializeToUtf8Bytes(HelpPresence?.Touch(sid, (int)session, (int)portalProcess) ?? 0);
                    var prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, presencePayload.Length);
                    await pipe.WriteAsync(prefix, timeout.Token); await pipe.WriteAsync(presencePayload, timeout.Token);
                    _ = await pipe.ReadAsync(new byte[1], timeout.Token);
                    continue;
                }

                if (command == PortalCommand.Help)
                {
                    string? sid = null;
                    pipe.RunAsClient(() => { using var caller = WindowsIdentity.GetCurrent(true); sid = caller?.User?.Value; });
                    if (sid is null) throw new UnauthorizedAccessException("Cannot identify the interactive user.");
                    if (WorkAccount is not null) sid = (await WorkAccount.ResolveAsync(sid, timeout.Token)).Sid;
                    var helpHeader = new byte[4]; await pipe.ReadExactlyAsync(helpHeader, timeout.Token);
                    var length = BinaryPrimitives.ReadInt32LittleEndian(helpHeader);
                    if (length is < 1 or > HelpAttachmentLimits.PayloadBytes) throw new IOException("Invalid help request size.");
                    var body = new byte[length]; await pipe.ReadExactlyAsync(body, timeout.Token);
                    var input = JsonSerializer.Deserialize<HelpRequest>(body) ?? throw new IOException("Invalid help request.");
                    var result = HelpRequestOverride is { } helpOverride
                        ? await helpOverride(sid, input, timeout.Token)
                        : await client.HelpAsync(sid, input, timeout.Token);
                    var helpPayload = JsonSerializer.SerializeToUtf8Bytes(result);
                    if (helpPayload.Length > HelpAttachmentLimits.PayloadBytes) throw new IOException("Help response too large.");
                    BinaryPrimitives.WriteInt32LittleEndian(helpHeader, helpPayload.Length);
                    await pipe.WriteAsync(helpHeader, timeout.Token); await pipe.WriteAsync(helpPayload, timeout.Token);
                    _ = await pipe.ReadAsync(new byte[1], timeout.Token);
                    continue;
                }

                if (command == PortalCommand.RemoteConsent)
                {
                    string key = "";
                    pipe.RunAsClient(() => { using var caller = WindowsIdentity.GetCurrent(true); if (caller is not null) key = AgentRemoteConsent.IdentityKey(caller); });
                    if (key.Length == 0 || !GetNamedPipeClientSessionId(pipe.SafePipeHandle, out var callerSession) || callerSession > int.MaxValue)
                        throw new UnauthorizedAccessException("Cannot verify the interactive user.");
                    var prefix = new byte[4]; await pipe.ReadExactlyAsync(prefix, timeout.Token);
                    var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
                    if (length is < 1 or > MaximumConsentRequestBytes) throw new IOException("Invalid consent request.");
                    var body = new byte[length]; await pipe.ReadExactlyAsync(body, timeout.Token);
                    var input = JsonSerializer.Deserialize<RemoteConsentInput>(body) ?? throw new IOException("Invalid consent request.");
                    var result = RemoteConsent?.Handle((int)callerSession, key, input) ?? new RemoteConsentStatus(null);
                    var consentPayload = JsonSerializer.SerializeToUtf8Bytes(result);
                    BinaryPrimitives.WriteInt32LittleEndian(prefix, consentPayload.Length);
                    await pipe.WriteAsync(prefix, timeout.Token); await pipe.WriteAsync(consentPayload, timeout.Token);
                    _ = await pipe.ReadAsync(new byte[1], timeout.Token);
                    continue;
                }

                if (command is >= PortalCommand.AdminSignIn and <= PortalCommand.Usb or PortalCommand.ImagePrep)
                {
                    string? adminSid = null;
                    pipe.RunAsClient(() =>
                    {
                        using var caller = WindowsIdentity.GetCurrent(true);
                        adminSid = caller?.User?.Value;
                    });

                    if (adminSid is null
                        || !GetNamedPipeClientSessionId(pipe.SafePipeHandle, out var windowsSession)
                        || windowsSession > int.MaxValue)
                        throw new UnauthorizedAccessException("Could not identify the administrator UI session.");

                    if (command == PortalCommand.ImagePrep)
                    {
                        AgentImagePrepStatus status;
                        try
                        {
                            var prefix = new byte[4];
                            await pipe.ReadExactlyAsync(prefix, timeout.Token);
                            var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
                            if (length is < 1 or > MaximumDriverPackRequestBytes)
                                throw new IOException("Invalid image prep request size.");

                            var body = new byte[length];
                            await pipe.ReadExactlyAsync(body, timeout.Token);
                            var input = JsonSerializer.Deserialize<AgentImagePrepRequest>(body)
                                ?? throw new IOException("Invalid image prep request.");
                            status = ImagePrep is null
                                ? new(PortalToolStatus.Failed, "Update the agent service to run image prep.")
                                : await ImagePrep.HandleAsync(adminSid, (int)windowsSession, input, timeout.Token);
                        }
                        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or IOException
                            or HttpRequestException or OperationCanceledException or JsonException)
                        {
                            status = new(PortalToolStatus.Failed, ex is UnauthorizedAccessException or InvalidOperationException
                                ? ex.Message : "Could not complete the image prep request. Check administrator sign-in and the server connection.");
                        }

                        var prepPayload = JsonSerializer.SerializeToUtf8Bytes(status);
                        if (prepPayload.Length > MaximumResponseBytes)
                            throw new IOException("Image prep response exceeded its size limit.");
                        var prepHeader = new byte[4];
                        BinaryPrimitives.WriteInt32LittleEndian(prepHeader, prepPayload.Length);
                        await pipe.WriteAsync(prepHeader, timeout.Token);
                        await pipe.WriteAsync(prepPayload, timeout.Token);
                        _ = await pipe.ReadAsync(new byte[1], timeout.Token);
                        continue;
                    }

                    if (command == PortalCommand.Usb)
                    {
                        AgentUsbStatus status;

                        try
                        {
                            var prefix = new byte[4];
                            await pipe.ReadExactlyAsync(prefix, timeout.Token);
                            var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);

                            if (length is < 1 or > MaximumUsbRequestBytes)
                                throw new IOException("Invalid USB request size.");

                            var body = new byte[length];
                            await pipe.ReadExactlyAsync(body, timeout.Token);
                            var input = JsonSerializer.Deserialize<AgentUsbRequest>(body)
                                        ?? throw new IOException("Invalid USB request.");
                            status = Usb is null
                                ? new(
                                    PortalToolStatus.Failed,
                                    "Update the agent service to create USB media.")
                                : await Usb.HandleAsync(adminSid, (int)windowsSession, input, timeout.Token);
                        }
                        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or IOException or HttpRequestException or OperationCanceledException or JsonException or System.ComponentModel.Win32Exception)
                        {
                            status = new(
                                PortalToolStatus.Error,
                                ex is UnauthorizedAccessException or InvalidOperationException
                                    ? ex.Message
                                    : "Could not complete the USB request. Check administrator sign-in, the server connection, and the agent service.");
                        }

                        var usbPayload = JsonSerializer.SerializeToUtf8Bytes(status);

                        if (usbPayload.Length > MaximumResponseBytes)
                            throw new IOException("USB response exceeded its size limit.");

                        var usbHeader = new byte[4];
                        BinaryPrimitives.WriteInt32LittleEndian(usbHeader, usbPayload.Length);
                        await pipe.WriteAsync(usbHeader, timeout.Token);
                        await pipe.WriteAsync(usbPayload, timeout.Token);
                        _ = await pipe.ReadAsync(new byte[1], timeout.Token);
                        continue;
                    }

                    if (command is PortalCommand.StartDriverPack or PortalCommand.DriverPackStatus or PortalCommand.CancelDriverPack)
                    {
                        AgentDriverPackStatus toolStatus;

                        try
                        {
                            AgentDriverPackRequest? input = null;

                            if (command == PortalCommand.StartDriverPack)
                            {
                                var prefix = new byte[4];
                                await pipe.ReadExactlyAsync(prefix, timeout.Token);
                                var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);

                                if (length is < 1 or > MaximumDriverPackRequestBytes)
                                    throw new IOException("Invalid driver pack request size.");

                                var body = new byte[length];
                                await pipe.ReadExactlyAsync(body, timeout.Token);
                                input = JsonSerializer.Deserialize<AgentDriverPackRequest>(body)
                                        ?? throw new IOException("Invalid driver pack request.");
                            }

                            toolStatus = DriverPacks is null
                                ? new(
                                    PortalToolStatus.Failed,
                                    "Update the agent service to use driver collection.")
                                : command switch
                                {
                                    PortalCommand.StartDriverPack => await DriverPacks.StartAsync(
                                        adminSid,
                                        (int)windowsSession,
                                        input!,
                                        timeout.Token),
                                    PortalCommand.DriverPackStatus => DriverPacks.Status(adminSid, (int)windowsSession),
                                    _ => DriverPacks.Cancel(adminSid, (int)windowsSession)
                                };
                        }
                        catch (UnauthorizedAccessException ex)
                        {
                            toolStatus = new(PortalToolStatus.Failed, ex.Message);
                        }
                        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or JsonException)
                        {
                            toolStatus = new(
                                PortalToolStatus.Failed,
                                "Could not start driver collection. Check administrator sign-in and the server connection.");
                        }

                        var toolPayload = JsonSerializer.SerializeToUtf8Bytes(toolStatus);
                        var prefixOut = new byte[4];
                        BinaryPrimitives.WriteInt32LittleEndian(prefixOut, toolPayload.Length);
                        await pipe.WriteAsync(prefixOut, timeout.Token);
                        await pipe.WriteAsync(toolPayload, timeout.Token);
                        _ = await pipe.ReadAsync(new byte[1], timeout.Token);
                        continue;
                    }

                    AgentAdminStatus adminStatus;

                    try
                    {
                        adminStatus = Admin is null
                            ? new(
                                "Unavailable",
                                Message: "Administrator sign-in is unavailable. Update the agent service.")
                            : command switch
                            {
                                PortalCommand.AdminSignIn => await Admin.StartAsync(
                                    adminSid,
                                    (int)windowsSession,
                                    timeout.Token),
                                PortalCommand.AdminStatus => await Admin.StatusAsync(
                                    adminSid,
                                    (int)windowsSession,
                                    timeout.Token),
                                _ => await Admin.SignOutAsync(adminSid, (int)windowsSession, timeout.Token)
                            };
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or JsonException)
                    {
                        adminStatus = new(
                            "Unavailable",
                            Message: "Could not connect to the server for administrator sign-in. Check the connection and try again.");
                    }

                    var adminPayload = JsonSerializer.SerializeToUtf8Bytes(adminStatus);
                    var adminHeader = new byte[4];
                    BinaryPrimitives.WriteInt32LittleEndian(adminHeader, adminPayload.Length);
                    await pipe.WriteAsync(adminHeader, timeout.Token);
                    await pipe.WriteAsync(adminPayload, timeout.Token);
                    _ = await pipe.ReadAsync(new byte[1], timeout.Token);
                    continue;
                }

                if (command is PortalCommand.Notifications or PortalCommand.AcknowledgeNotification or PortalCommand.NotificationSnapshot)
                {
                    string? messageSid = null;
                    pipe.RunAsClient(() =>
                    {
                        using var caller = WindowsIdentity.GetCurrent(true);
                        messageSid = caller?.User?.Value;
                    });

                    if (messageSid is null)
                        throw new UnauthorizedAccessException("Could not identify the message recipient.");

                    if (WorkAccount is not null)
                    {
                        try
                        {
                            var resolved = await WorkAccount.ResolveAsync(messageSid, timeout.Token);
                            messageSid = resolved.Sid;
                            userName = resolved.Identity.User?.Name ?? userName;
                        }
                        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
                        { /* Offline notices remain scoped to the local Windows identity. */ }
                    }
                    if (command == PortalCommand.AcknowledgeNotification)
                    {
                        var ackHeader = new byte[4];
                        await pipe.ReadExactlyAsync(ackHeader, timeout.Token);
                        var length = BinaryPrimitives.ReadInt32LittleEndian(ackHeader);

                        if (length is < 1 or > MaximumAcknowledgmentBytes)
                            throw new IOException("Invalid acknowledgment request size.");

                        var data = new byte[length];
                        await pipe.ReadExactlyAsync(data, timeout.Token);
                        var id = JsonSerializer.Deserialize<string>(data) ?? "";
                        var accepted = Notifications?.Acknowledge(id, messageSid, userName) == true;
                        var response = JsonSerializer.SerializeToUtf8Bytes(accepted);
                        BinaryPrimitives.WriteInt32LittleEndian(ackHeader, response.Length);
                        await pipe.WriteAsync(ackHeader, timeout.Token);
                        await pipe.WriteAsync(response, timeout.Token);
                        _ = await pipe.ReadAsync(new byte[1], timeout.Token);
                        continue;
                    }

                    var messages = Notifications?.ReadForUser(messageSid, userName) ?? [];
                    // Check before first presentation, then at most once per 30 seconds per user.
                    // An unavailable server never blocks local acknowledgement or offline notices.
                    var pendingNotices = messages.Where(x => x.PendingAcknowledgment).ToArray();
                    if (Notifications is not null && pendingNotices.Any(x =>
                        !messageSyncTimes.TryGetValue($"{messageSid}:{x.Id}", out var checkedAt) || checkedAt.AddSeconds(30) <= DateTimeOffset.UtcNow))
                    {
                        if (messageSyncTimes.Count > MaximumMessageSyncEntries) messageSyncTimes.Clear();
                        foreach (var notice in pendingNotices) messageSyncTimes[$"{messageSid}:{notice.Id}"] = DateTimeOffset.UtcNow;
                        using var syncTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                        syncTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                        try
                        {
                            var reports = Notifications.PendingReports(messageSid);
                            if (reports.Length > 0)
                            {
                                var acknowledgments = await client.ReportNotificationsAsync(reports, syncTimeout.Token);
                                foreach (var report in reports) Notifications.MarkReported(report);
                                Notifications.ApplyAcknowledgments(acknowledgments);
                                messages = Notifications.ReadForUser(messageSid, userName);
                            }
                        }
                        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException or JsonException)
                        { logger.LogDebug(ex, "Using local message acknowledgments while server sync is unavailable"); }
                    }
                    var notices = command == PortalCommand.NotificationSnapshot
                        ? JsonSerializer.SerializeToUtf8Bytes(new AgentNotificationSnapshot(
                            messages,
                            new AgentBrandingCache(options).Read()) { Features = new AgentFeatureCache(options).Read() })
                        : JsonSerializer.SerializeToUtf8Bytes(messages);

                    if (notices.Length > MaximumResponseBytes)
                        throw new IOException("Message history exceeded its size limit.");

                    var prefix = new byte[4];
                    BinaryPrimitives.WriteInt32LittleEndian(prefix, notices.Length);
                    await pipe.WriteAsync(prefix, timeout.Token);
                    await pipe.WriteAsync(notices, timeout.Token);
                    _ = await pipe.ReadAsync(new byte[1], timeout.Token);
                    continue;
                }

                AgentPortalSnapshot snapshot;

                try
                {
                    string? sid = null;
                    // Identification-level impersonation only inspects the caller's token.
                    // Revert before any network request or access to service credentials.
                    pipe.RunAsClient(() =>
                    {
                        using var caller = WindowsIdentity.GetCurrent(true);
                        sid = caller?.User?.Value;
                    });

                    if (sid is null)
                        throw new IOException("Could not identify the portal caller's SID.");

                    using var metadataTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                    metadataTimeout.CancelAfter(MetadataTimeout);

                    if (cachedPolicies is null || DateTimeOffset.UtcNow >= cacheExpires)
                    {
                        cachedPolicies = await client.GetPortalPoliciesAsync(metadataTimeout.Token);
                        cacheExpires = DateTimeOffset.UtcNow + MetadataCacheLifetime;
                    }

                    if (cachedSid != sid || cachedDirectoryIdentity is null
                        || DateTimeOffset.UtcNow >= identityCacheExpires)
                    {
                        var resolved = WorkAccount is null ? (Sid: sid, Identity: await client.GetPortalIdentityAsync(sid, metadataTimeout.Token))
                            : await WorkAccount.ResolveAsync(sid, metadataTimeout.Token);
                        var directoryIdentity = resolved.Identity;
                        var available = await client.GetAvailablePoliciesAsync(resolved.Sid, metadataTimeout.Token);
                        cachedDirectoryIdentity = directoryIdentity;
                        cachedAvailable = available;
                        cachedSid = sid;
                        identityCacheExpires = DateTimeOffset.UtcNow + MetadataCacheLifetime;
                    }

                    if (command == PortalCommand.PolicyIcon)
                    {
                        var idBytes = new byte[4];
                        await pipe.ReadExactlyAsync(idBytes, timeout.Token);
                        var id = BinaryPrimitives.ReadInt32LittleEndian(idBytes);
                        // Only icons represented by this caller's current authorized catalog may leave the service.
                        var hash = cachedAvailable.FirstOrDefault(p => p.Id == id)?.IconHash;
                        byte[] icon = [];

                        if (hash is not null)
                        {
                            if (!iconCache.TryGetValue(hash, out icon!))
                            {
                                var iconSid = WorkAccount is null ? sid : (await WorkAccount.ResolveAsync(sid, metadataTimeout.Token)).Sid;
                                icon = await client.GetPortalIconAsync(id, iconSid, metadataTimeout.Token);

                                if (!Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(icon)).Equals(
                                        hash,
                                        StringComparison.OrdinalIgnoreCase))
                                    throw new IOException("Portal icon changed; refresh the catalog.");

                                if (iconCache.Count >= MaximumCachedIcons)
                                    iconCache.Clear();

                                iconCache[hash] = icon;
                            }
                        }

                        var prefix = new byte[4];
                        BinaryPrimitives.WriteInt32LittleEndian(prefix, icon.Length);
                        await pipe.WriteAsync(prefix, timeout.Token);
                        await pipe.WriteAsync(icon, timeout.Token);
                        _ = await pipe.ReadAsync(new byte[1], timeout.Token);
                        continue;
                    }

                    snapshot = new(
                        userName,
                        Environment.MachineName,
                        options.GetServerUri().AbsoluteUri,
                        cachedPolicies,
                        DirectoryIdentity: cachedDirectoryIdentity)
                    {
                        AvailablePolicies = cachedAvailable
                    };
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or JsonException)
                {
                    logger.LogWarning(
                        $"Portal metadata request failed: {ex.GetType().Name}, HTTP status {(ex as HttpRequestException)?.StatusCode}, transport {(ex as HttpRequestException)?.HttpRequestError}");
                    logger.LogDebug($"Portal transport detail: {ex.GetBaseException().Message}");
                    snapshot = new(
                        userName,
                        Environment.MachineName,
                        options.GetServerUri().AbsoluteUri,
                        [],
                        "Could not load policies. Check the agent's enrollment and server connection, then refresh.");
                }

                snapshot = snapshot with
                {
                    AgentVersion = typeof(AgentPortalService).Assembly.GetName().Version?.ToString(3)
                };
                var payload = JsonSerializer.SerializeToUtf8Bytes(snapshot);

                if (payload.Length > MaximumResponseBytes)
                    throw new IOException("Portal response exceeded its size limit.");

                var header = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
                await pipe.WriteAsync(header, timeout.Token);
                await pipe.WriteAsync(payload, timeout.Token);
                // Wait for receipt (or an older client closing) before Disconnect, which
                // otherwise discards bytes the client has not consumed yet.
                _ = await pipe.ReadAsync(new byte[1], timeout.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning($"Portal connection ended: {ex.GetType().Name}: {ex.Message}");

                try
                {
                    await Task.Delay(ListenerRetryDelay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                // Keep ownership of the pipe between polls. Recreating a first-instance
                // pipe can race with the previous client's handle closing.
                try
                {
                    if (connected)
                        pipe!.Disconnect();
                }
                catch (IOException)
                {
                    pipe?.Dispose();
                    pipe = null;
                }
            }
        }

        pipe?.Dispose();
    }
}
