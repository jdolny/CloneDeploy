using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent;

/// <summary>
/// Describes how to run an action using a selected user account.
/// </summary>
public interface IRunAsProcess
{
    Task<PolicyProcessResult> RunAsync(
        ProcessStartInfo start,
        AgentPolicyCredential credential,
        string cacheDirectory,
        int timeoutMinutes,
        CancellationToken ct);
}

/// <summary>
/// Runs an action as a selected user and cleans up the temporary task and permissions afterward.
/// </summary>
public sealed class ScheduledRunAsProcess : IRunAsProcess
{
    private const int MaximumTimeoutMinutes = 24 * 60;
    private const int MaximumResultBytes = 64 * 1024;
    private static readonly TimeSpan ResultPollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);

    private static class MembershipState
    {
        public const string Unchanged = "Unchanged";
        public const string Adding = "Adding";
        public const string Added = "Added";
    }

    private readonly RunAsJournal journal;
    private readonly IRunAsWindows windows;
    private readonly ILogger<ScheduledRunAsProcess> logger;
    private readonly string root;
    private readonly string instance;
    private readonly SemaphoreSlim gate = new(1, 1);

    public ScheduledRunAsProcess(
        AgentOptions options,
        IRunAsWindows windows,
        ILogger<ScheduledRunAsProcess> logger,
        string? stagingRoot = null)
    {
        journal = new RunAsJournal(options);
        this.windows = windows;
        this.logger = logger;
        instance = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(options.StateDirectory).ToUpperInvariant())))[..16];
        root = Path.Combine(
            stagingRoot
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "CloneDeploy",
                "RunAs"),
            instance);
    }

    private string TaskName(Guid id) => $"CloneDeploy.RunAs.{instance}.{id.ToString("N")}";

    private string DirectoryFor(Guid id)
    {
        var path = Path.GetFullPath(Path.Combine(root, id.ToString("N")));

        if (!path.StartsWith($"{Path.GetFullPath(root)}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid run-as staging directory.");

        return path;
    }

    public async Task RecoverAsync()
    {
        await gate.WaitAsync();

        try
        {
            await RecoverCoreAsync();
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task RecoverCoreAsync()
    {
        foreach (var record in journal.Read())
        {
            logger.LogWarning($"Recovering temporary run-as task {record.Id}");
            await CleanupAsync(record);
        }
    }

    public async Task<PolicyProcessResult> RunAsync(
        ProcessStartInfo start,
        AgentPolicyCredential credential,
        string cacheDirectory,
        int timeoutMinutes,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(credential.Username) || string.IsNullOrEmpty(credential.Password))
            throw new InvalidDataException("Run-as username and password are required.");

        if (timeoutMinutes < 0 || timeoutMinutes > MaximumTimeoutMinutes)
            throw new InvalidDataException("Run-as timeout must be between 0 and 1440 minutes.");

        await gate.WaitAsync(ct);
        RunAsRecovery? record = null;

        try
        {
            await RecoverCoreAsync();
            var sid = windows.ResolveAccount(credential.Username);
            record = new(Guid.NewGuid(), sid, MembershipState.Unchanged);
            journal.Save(record);
            var directory = DirectoryFor(record.Id);
            PrepareDirectory(directory, new SecurityIdentifier(sid));
            var work = Path.Combine(directory, "work");
            Directory.CreateDirectory(work);
            var output = Path.Combine(directory, "output");
            Directory.CreateDirectory(output);
            SetPermissions(work, new SecurityIdentifier(sid), FileSystemRights.Modify);
            SetPermissions(output, new SecurityIdentifier(sid), FileSystemRights.Modify);

            foreach (var file in Directory.EnumerateFiles(cacheDirectory))
            {
                PolicyActionRunner.RejectReparsePoints(file);
                File.Copy(file, Path.Combine(work, Path.GetFileName(file)));
            }

            var original = Path.GetFullPath(cacheDirectory).TrimEnd(Path.DirectorySeparatorChar);

            string Relocate(string value) => value.Replace(
                $"{original}{Path.DirectorySeparatorChar}",
                $"{work}{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase);

            var working = Path.GetFullPath(start.WorkingDirectory);

            if (working.TrimEnd(Path.DirectorySeparatorChar).Equals(original, StringComparison.OrdinalIgnoreCase))
                working = work;

            var minutes = timeoutMinutes == 0 ? ActionTimeout.DefaultMinutes : timeoutMinutes;
            var request = new
            {
                Executable = Relocate(start.FileName),
                Arguments = Relocate(start.Arguments),
                WorkingDirectory = working,
                Stdout = start.RedirectStandardOutput,
                Stderr = start.RedirectStandardError,
                TimeoutSeconds = minutes * 60
            };
            File.WriteAllText(
                Path.Combine(directory, "request.json"),
                JsonSerializer.Serialize(request),
                new UTF8Encoding(false));
            using (var resource = typeof(ScheduledRunAsProcess).Assembly.GetManifestResourceStream("CloneDeploy_Agent.RunAsHelper.ps1")
                                  ?? throw new InvalidOperationException("Run-as helper is missing."))
            using (var script = File.Create(Path.Combine(directory, "runner.ps1")))
                resource.CopyTo(script);

            if (!windows.IsBackupOperator(sid))
            {
                record = record with
                {
                    MembershipState = MembershipState.Adding
                };
                journal.Save(record);
                var added = windows.AddBackupOperator(sid);
                record = record with
                {
                    MembershipState = added ? MembershipState.Added : MembershipState.Unchanged
                };
                journal.Save(record);
                logger.LogInformation(
                    $"Run-as {record.Id}: Backup Operators membership {(added ? "temporarily added" : "already present")}");
            }
            else
                logger.LogInformation($"Run-as {record.Id}: preserving existing Backup Operators membership");

            ct.ThrowIfCancellationRequested();
            windows.Register(
                TaskName(record.Id),
                Path.Combine(directory, "runner.ps1"),
                directory,
                credential.Username,
                credential.Password,
                minutes);
            logger.LogInformation($"Starting scheduled run-as task {record.Id} for {credential.Username}");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(minutes));
            windows.Start(TaskName(record.Id));
            var resultPath = Path.Combine(output, "result.json");
            var seenActive = false;
            var startDeadline = DateTime.UtcNow + StartupTimeout;

            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                var active = windows.IsActive(TaskName(record.Id));
                seenActive |= active;

                if (!active && File.Exists(resultPath))
                    break;

                if (!active && (seenActive || DateTime.UtcNow >= startDeadline))
                    throw new InvalidOperationException($"Scheduled run-as task ended without an execution result (scheduler code {windows.LastResult(TaskName(record.Id))}).");

                await Task.Delay(ResultPollInterval, timeout.Token);
            }

            PolicyActionRunner.RejectReparsePoints(resultPath);

            if (new FileInfo(resultPath).Length > MaximumResultBytes)
                throw new InvalidDataException("Run-as result exceeded its size limit.");

            var result = JsonSerializer.Deserialize<RunAsResult>(await File.ReadAllTextAsync(resultPath, timeout.Token))
                         ?? throw new InvalidDataException("Missing run-as result.");

            if (result.TimedOut)
                throw new OperationCanceledException("Run-as execution timed out.");

            logger.LogInformation($"Scheduled run-as task {record.Id} completed with exit code {result.ExitCode}");

            string Clean(string? value) => (value ?? "").Replace(
                credential.Password,
                "[redacted]",
                StringComparison.Ordinal);

            return new(
                result.ExitCode,
                start.RedirectStandardOutput ? Clean(result.Output) : "",
                start.RedirectStandardError ? Clean(result.Error) : "");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                $"Run-as {record?.Id} failed ({ex.GetType().Name}, Windows error {(ex is System.ComponentModel.Win32Exception native ? native.NativeErrorCode : ex.HResult)})");
            throw;
        }
        finally
        {
            try
            {
                if (record is not null)
                    await CleanupAsync(record);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    $"Run-as {record?.Id}: cleanup remains pending ({ex.GetType().Name}); recovery will retry");
                throw new OperationCanceledException(
                    "Run-as cleanup did not complete; execution outcome requires review.",
                    ex);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private async Task CleanupAsync(RunAsRecovery record)
    {
        await windows.StopAndDeleteAsync(TaskName(record.Id));

        if (record.MembershipState == MembershipState.Adding && windows.IsBackupOperator(record.AccountSid))
            throw new InvalidOperationException("Run-as membership addition was interrupted before ownership could be confirmed. Review Backup Operators membership and the run-as recovery journal before retrying.");

        if (record.MembershipState == MembershipState.Added)
        {
            windows.RemoveBackupOperator(record.AccountSid);
            journal.Save(record with
            {
                MembershipState = MembershipState.Unchanged
            });
            logger.LogInformation($"Run-as {record.Id}: removed membership added by this agent");
        }

        var directory = DirectoryFor(record.Id);
        PolicyActionRunner.RejectReparsePoints(directory);

        if (Directory.Exists(directory))
        {
            CheckTree(directory);
            Directory.Delete(directory, true);
        }

        journal.Delete(record.Id);
        logger.LogInformation($"Run-as {record.Id}: temporary task and files cleaned up");
    }

    private static void CheckTree(string directory)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            PolicyActionRunner.RejectReparsePoints(path);

            if (Directory.Exists(path))
                CheckTree(path);
        }
    }

    private void PrepareDirectory(string directory, SecurityIdentifier account)
    {
        PolicyActionRunner.RejectReparsePoints(root);
        Directory.CreateDirectory(root);
        SetPermissions(root, null, 0);
        Directory.CreateDirectory(directory);
        SetPermissions(directory, account, FileSystemRights.ReadAndExecute);
    }

    private static void SetPermissions(string directory, SecurityIdentifier? account, FileSystemRights rights)
    {
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        using var current = WindowsIdentity.GetCurrent();

        foreach (var sid in new[]
                 {
                     current.User!,
                     new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                     new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
                 }.Distinct())
            acl.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));

        if (account is not null)
            acl.AddAccessRule(new FileSystemAccessRule(
                account,
                rights,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
        else
            acl.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                FileSystemRights.Traverse,
                AccessControlType.Allow));

        new DirectoryInfo(directory).SetAccessControl(acl);
    }

    /// <summary>
    /// Holds the outcome and captured output of an action run as another user.
    /// </summary>
    private sealed record RunAsResult(int ExitCode, string? Output, string? Error, bool TimedOut);
}