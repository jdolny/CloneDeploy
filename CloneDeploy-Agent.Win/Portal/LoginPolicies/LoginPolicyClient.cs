using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Security.Principal;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent.UI;

/// <summary>Executes service-selected login actions with this user's ordinary Windows permissions.</summary>
internal static class LoginPolicyClient
{
    private const int PipeConnectTimeoutMilliseconds = 3000;
    private const int FileTransferBufferBytes = 65536;

    internal static Task RunAsync(Action<AgentPortalNotification> showMessage, CancellationToken ct) => RunCoreAsync(showMessage, ct, null);

    internal static Task RunRequestedAsync(int policyId, Action<AgentPortalNotification> showMessage, CancellationToken ct)
        => RunCoreAsync(showMessage, ct, policyId);

    private static async Task RunCoreAsync(Action<AgentPortalNotification> showMessage, CancellationToken ct, int? policyId)
    {
        var requestId = Guid.NewGuid();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", LoginPolicyWire.PipeName, PipeAccessRights.ReadWrite,
                    PipeOptions.Asynchronous, TokenImpersonationLevel.Identification, HandleInheritability.None);
                await pipe.ConnectAsync(PipeConnectTimeoutMilliseconds, ct);
                PortalServiceIdentity.Verify(pipe.SafePipeHandle);
                await LoginPolicyWire.WriteAsync(pipe, typeof(App).Assembly.GetName().Version!.ToString() + (policyId.HasValue ? $"|{policyId}|{requestId}" : ""), ct);
                while (true)
                {
                    var command = await LoginPolicyWire.ReadAsync<LoginPolicyCommand>(pipe, ct);
                    if (command.Kind is "Complete" or "Retry" or "Failed")
                    {
                        await pipe.WriteAsync(new byte[] { 0 }, ct);
                        if (command.Kind == "Complete") return;
                        if (policyId.HasValue) throw new InvalidOperationException(command.Kind == "Failed"
                            ? PortalText.Get("SelfService.PolicyUnsuccessful")
                            : PortalText.Get("SelfService.RequestFailed"));
                        break;
                    }
                    if (command.Kind != "Action" || command.Action is not { } action)
                        throw new InvalidDataException(PortalText.Get("LoginPolicies.InvalidCommand"));
                    PolicyUserContext.ValidateAction(action);
                    var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "CloneDeploy", "Portal", "LoginCache", Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(directory);
                    try
                    {
                        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var file in action.Files)
                        {
                            if (file.Size < 0 || string.IsNullOrWhiteSpace(file.Name) || file.Name != Path.GetFileName(file.Name)
                                || file.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !names.Add(file.Name))
                                throw new InvalidDataException(PortalText.Get("LoginPolicies.InvalidFile"));
                            var path = Path.Combine(directory, file.Name);
                            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, FileTransferBufferBytes, true))
                            {
                                var buffer = new byte[FileTransferBufferBytes];
                                for (long remaining = file.Size; remaining > 0;)
                                {
                                    var count = await pipe.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
                                    if (count == 0) throw new EndOfStreamException();
                                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                                    remaining -= count;
                                }
                            }
                            if (!await PolicyCache.MatchesAsync(path, file, ct)) throw new InvalidDataException(PortalText.Get("LoginPolicies.FileChecksumMismatch"));
                        }
                        using var actionStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        using var monitorStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        // The service sends no more bytes until the result. EOF cancels child processes on service exit.
                        var disconnected = WatchDisconnectAsync(pipe, actionStop, monitorStop.Token);
                        PolicyProcessResult result;
                        try
                        {
                            if (action.Type == 6)
                            {
                                showMessage(new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
                                    action.Settings.Text("Title"), action.Settings.Text("Message"), action.Settings.Number("Timeout") == 0 ? 60 : action.Settings.Number("Timeout")));
                                result = new(0, PortalText.Get("RemoteSupport.MessageQueued"), "");
                            }
                            else
                            {
                                using var client = new HttpClient(new NoServerRequests());
                                result = await Task.Run(() => new PolicyActionRunner(new PolicyProcess()) { UserContext = true }
                                    .RunAsync(action, directory, client, Guid.NewGuid(), actionStop.Token), actionStop.Token);
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { result = new(1, "", ex.Message); }
                        finally { monitorStop.Cancel(); await disconnected; }
                        actionStop.Token.ThrowIfCancellationRequested();
                        await LoginPolicyWire.WriteAsync(pipe, new LoginPolicyResult(result.ExitCode, result.Output, result.Error), ct);
                    }
                    finally
                    {
                        // Actions may create links; do not follow them while removing the per-action directory.
                        try { PolicyActionRunner.RejectReparsePoints(directory); Directory.Delete(directory, true); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested && !policyId.HasValue) { return; }
            catch (Exception ex) { if (policyId.HasValue) throw; System.Diagnostics.Trace.TraceWarning(PortalText.Get("LoginPolicies.ExecutionError"), ex.Message); }
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static async Task WatchDisconnectAsync(Stream pipe, CancellationTokenSource action, CancellationToken ct)
    {
        try { _ = await pipe.ReadAsync(new byte[1], ct); action.Cancel(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (IOException) { action.Cancel(); }
    }

    private sealed class NoServerRequests : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException(PortalText.Get("LoginPolicies.ServerCredentialsUnavailable"));
    }
}
