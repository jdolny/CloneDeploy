using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent;

/// <summary>Admits login policy requests from Windows users and delegates only user-context actions to their portal.</summary>
public sealed class LoginPolicyService(AgentPolicyRunner policies, ILogger<LoginPolicyService> logger) : BackgroundService
{
    public AgentWorkAccount? WorkAccount { get; init; }
    private const int PipeBufferBytes = 65536;

    private const int MaximumConnections = 16;
    private const int MaximumOutputLength = 4000;
    private const int ActionTransferAllowanceMinutes = 10;
    private static readonly TimeSpan ConnectionLifetime = TimeSpan.FromHours(8);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ErrorReplyTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ListenerRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FinalResponseTimeout = TimeSpan.FromSeconds(5);

    private static NamedPipeServerStream Create(bool first)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Deny));
        using var owner = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new(owner.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            LoginPolicyWire.PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None),
            PipeBufferBytes,
            PipeBufferBytes,
            security);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connections = new List<Task>();
        NamedPipeServerStream? listener = null;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    listener ??= Create(connections.Count == 0);
                    await listener.WaitForConnectionAsync(stoppingToken);
                    var connected = listener;
                    // Reserve the next instance before releasing the current one.
                    listener = Create(false);
                    connections.RemoveAll(x => x.IsCompleted);

                    if (connections.Count >= MaximumConnections)
                    {
                        connected.Dispose();
                        continue;
                    }

                    connections.Add(HandleAsync(connected, stoppingToken));
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogWarning($"Login policy listener unavailable ({ex.GetType().Name})");
                    listener?.Dispose();
                    listener = null;
                    await Task.Delay(ListenerRetryDelay, stoppingToken);
                }
            }
        }
        finally
        {
            listener?.Dispose();
            await Task.WhenAll(connections);
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        using (pipe)
        using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
        {
            // A user connection cannot retain the device's execution slot indefinitely.
            lifetime.CancelAfter(ConnectionLifetime);

            try
            {
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                handshake.CancelAfter(HandshakeTimeout);
                var request = (await LoginPolicyWire.ReadAsync<string>(pipe, handshake.Token)).Split('|');
                var version = request[0];
                int? policyId = null;
                var requestId = Guid.Empty;

                if (request.Length != 1)
                {
                    if (request.Length != 3 || !int.TryParse( request[1], out var id) || id <= 0
                        || !Guid.TryParse(request[2], out requestId)
                        || requestId == Guid.Empty)
                        throw new InvalidDataException("Invalid manual request.");

                    policyId = id;
                }

                var user = LoginPipeIdentity.Read(pipe);

                if (version != typeof(LoginPolicyService).Assembly.GetName().Version!.ToString()
                    || !policies.LoginAllowed)
                {
                    await FinishAsync(pipe, "Retry", lifetime.Token);

                    return;
                }

                var workSid = WorkAccount is null ? user.Sid : (await WorkAccount.ResolveAsync(user.Sid, lifetime.Token)).Sid;
                var context = new LoginPolicyContext(
                    workSid,
                    user.Name,
                    workSid == user.Sid ? user.LogonKey : user.LogonKey + "|" + workSid,
                    async (action, directory, ct) =>
                    {
                        PolicyUserContext.ValidateAction(action);
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        deadline.CancelAfter(TimeSpan.FromMinutes((action.Settings.Number("Timeout") == 0
                            ? ActionTimeout.DefaultMinutes
                            : action.Settings.Number("Timeout")) + ActionTransferAllowanceMinutes));

                        try
                        {
                            await LoginPolicyWire.WriteAsync(
                                pipe,
                                new LoginPolicyCommand("Action", action),
                                deadline.Token);

                            foreach (var file in action.Files)
                            {
                                var path = Path.Combine(directory, file.Name);
                                PolicyActionRunner.RejectReparsePoints(path);
                                await using var input = File.OpenRead(path);

                                if (input.Length != file.Size)
                                    throw new InvalidDataException("Login action cache size changed.");

                                await input.CopyToAsync(pipe, deadline.Token);
                            }

                            var result = await LoginPolicyWire.ReadAsync<LoginPolicyResult>(
                                pipe,
                                deadline.Token);

                            return new PolicyProcessResult(
                                result.ExitCode,
                                (result.Output ?? "")[..Math.Min(result.Output?.Length ?? 0, MaximumOutputLength)],
                                (result.Error ?? "")[..Math.Min(result.Error?.Length ?? 0, MaximumOutputLength)]);
                        }
                        catch (IOException ex)
                        {
                            throw new OperationCanceledException(
                                "User session disconnected during execution; outcome is uncertain.",
                                ex);
                        }
                    })
                {
                    RequestedPolicyId = policyId,
                    RequestId = requestId
                };

                if (policyId.HasValue)
                    await policies.RunRequestedAsync(context, lifetime.Token);
                else
                    await policies.RunLoginAsync(context, lifetime.Token);

                await FinishAsync(
                    pipe,
                    policyId.HasValue && context.Result != 0 ? "Failed" : "Complete",
                    lifetime.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    $"Login policy request ended ({ex.GetType().Name}): {(ex is UnauthorizedAccessException
                        ? ex.Message
                        : "See policy history for execution results.")}");

                try
                {
                    using var reply = new CancellationTokenSource(ErrorReplyTimeout);
                    await FinishAsync(pipe, "Retry", reply.Token);
                }
                catch
                {
                }
            }
        }
    }

    private static async Task FinishAsync(Stream pipe, string kind, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(FinalResponseTimeout);
        await LoginPolicyWire.WriteAsync(pipe, new LoginPolicyCommand(kind), deadline.Token);
        // Keep the server handle alive until the client has consumed the final response.
        await pipe.ReadExactlyAsync(new byte[1], deadline.Token);
    }
}
