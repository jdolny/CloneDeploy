using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace CloneDeploy_Agent;

/// <summary>
/// Describes the Windows operations needed to run an action as another user.
/// </summary>
public interface IRunAsWindows
{
    string ResolveAccount(string username);

    bool IsBackupOperator(string sid);

    bool AddBackupOperator(string sid);

    void RemoveBackupOperator(string sid);

    void Register(
        string taskName,
        string script,
        string workingDirectory,
        string username,
        string password,
        int timeoutMinutes);

    void Start(string taskName);

    bool IsActive(string taskName);

    int LastResult(string taskName);

    Task StopAndDeleteAsync(string taskName);
}

/// <summary>
/// Manages the Windows accounts, group membership, and scheduled tasks needed to run as another user.
/// </summary>
public sealed class RunAsWindows : IRunAsWindows
{
    private const int MoreData = 234;
    private const int MemberNotInAlias = 1377;
    private const int MemberAlreadyInAlias = 1378;
    private const int HighestRunLevel = 1;
    private const int IgnoreNewInstances = 2;
    private const int ExecuteAction = 0;
    private const int CreateTask = 2;
    private const int PasswordLogon = 1;
    private const int QueuedTaskState = 2;
    private const int RunningTaskState = 4;
    private const int FileNotFoundHResult = unchecked((int)0x80070002);
    private const int TaskNotRunningHResult = unchecked((int)0x8004130B);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StopPollInterval = TimeSpan.FromMilliseconds(200);

    private static string Group => ((NTAccount)new SecurityIdentifier(WellKnownSidType.BuiltinBackupOperatorsSid, null)
        .Translate(typeof(NTAccount))).Value.Split('\\').Last();

    public string ResolveAccount(string username) => ((SecurityIdentifier)new NTAccount(
            username.StartsWith(@".\", StringComparison.Ordinal)
                ? $"{Environment.MachineName}{username[1..]}"
                : username)
        .Translate(typeof(SecurityIdentifier))).Value;

    public bool IsBackupOperator(string sid)
    {
        nint resume = 0;

        while (true)
        {
            var status = NetLocalGroupGetMembers(null, Group, 0, out var buffer, -1, out var read, out _, ref resume);

            if (status is not (0 or MoreData))
                throw new Win32Exception(status);

            try
            {
                for (var i = 0; i < read; i++)
                    if (new SecurityIdentifier(Marshal.ReadIntPtr(buffer, i * IntPtr.Size)).Value == sid)
                        return true;
            }
            finally
            {
                if (buffer != IntPtr.Zero) NetApiBufferFree(buffer);
            }

            if (status == 0)
                return false;
        }
    }

    public bool AddBackupOperator(string sid)
    {
        var status = ChangeMember(sid, true);

        if (status == MemberAlreadyInAlias)
            return false; // Already a direct member; do not take ownership.
        if (status != 0)
            throw new Win32Exception(status);

        return true;
    }

    public void RemoveBackupOperator(string sid)
    {
        var status = ChangeMember(sid, false);

        if (status is not (0 or MemberNotInAlias))
            throw new Win32Exception(status);
    }

    private static int ChangeMember(string sid, bool add)
    {
        var identifier = new SecurityIdentifier(sid);
        var bytes = new byte[identifier.BinaryLength];
        identifier.GetBinaryForm(bytes, 0);
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);

        try
        {
            var member = pinned.AddrOfPinnedObject();

            return add
                ? NetLocalGroupAddMembers(null, Group, 0, ref member, 1)
                : NetLocalGroupDelMembers(null, Group, 0, ref member, 1);
        }
        finally
        {
            pinned.Free();
        }
    }

    public void Register(
        string taskName,
        string script,
        string workingDirectory,
        string username,
        string password,
        int timeoutMinutes)
    {
        using var com = new ComObjects();
        dynamic service = com.Service();
        dynamic root = com.Track(service.GetFolder("\\"));
        dynamic definition = com.Track(service.NewTask(0));
        dynamic principal = com.Track(definition.Principal);
        principal.RunLevel = HighestRunLevel; // Highest available for the selected account.
        dynamic settings = com.Track(definition.Settings);
        settings.Enabled = true;
        settings.AllowDemandStart = true;
        settings.DisallowStartIfOnBatteries = false;
        settings.StopIfGoingOnBatteries = false;
        settings.AllowHardTerminate = true;
        settings.MultipleInstances = IgnoreNewInstances;
        settings.ExecutionTimeLimit = System.Xml.XmlConvert.ToString(TimeSpan.FromMinutes(timeoutMinutes + 1));
        dynamic actions = com.Track(definition.Actions);
        dynamic action = com.Track(actions.Create(ExecuteAction));
        action.Path = PolicyActionRunner.PowerShell;
        action.Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File {PolicyActionRunner.Quote(script)}";
        action.WorkingDirectory = workingDirectory;
        // Password goes directly to Task Scheduler, never into arguments, files, or our journal.
        com.Track(root.RegisterTaskDefinition(
            taskName,
            definition,
            CreateTask,
            username,
            password,
            PasswordLogon,
            null));
    }

    public void Start(string taskName)
    {
        using var com = new ComObjects();
        dynamic service = com.Service();
        dynamic root = com.Track(service.GetFolder("\\"));
        dynamic task = com.Track(root.GetTask(taskName));
        com.Track(task.Run(null));
    }

    public bool IsActive(string taskName)
    {
        using var com = new ComObjects();
        dynamic service = com.Service();
        dynamic root = com.Track(service.GetFolder("\\"));
        dynamic task = com.Track(root.GetTask(taskName));
        dynamic instances = com.Track(task.GetInstances(0));

        return instances.Count > 0 || task.State == QueuedTaskState || task.State == RunningTaskState;
    }

    public int LastResult(string taskName)
    {
        using var com = new ComObjects();
        dynamic service = com.Service();
        dynamic root = com.Track(service.GetFolder("\\"));
        dynamic task = com.Track(root.GetTask(taskName));

        return (int)task.LastTaskResult;
    }

    public async Task StopAndDeleteAsync(string taskName)
    {
        using var com = new ComObjects();
        dynamic service = com.Service();
        dynamic root = com.Track(service.GetFolder("\\"));
        dynamic task;

        try
        {
            task = com.Track(root.GetTask(taskName));
        }
        catch (Exception ex) when (ex.HResult == FileNotFoundHResult)
        {
            return;
        }

        task.Enabled = false;

        try
        {
            task.Stop(0);
        }
        catch (COMException ex) when (ex.HResult == TaskNotRunningHResult)
        {
        }

        var end = DateTime.UtcNow + StopTimeout;

        while (IsActive(taskName))
        {
            if (DateTime.UtcNow >= end)
                throw new TimeoutException("Scheduled run-as task did not stop; cleanup remains pending.");

            await Task.Delay(StopPollInterval);
        }

        root.DeleteTask(taskName, 0);
    }

    /// <summary>
    /// Keeps track of Windows scheduler resources and releases them when finished.
    /// </summary>
    private sealed class ComObjects : IDisposable
    {
        private readonly List<object> objects = [];

        public dynamic Track(object value)
        {
            objects.Add(value);

            return value;
        }

        public dynamic Service()
        {
            dynamic service = Track(Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!);
            service.Connect();

            return service;
        }

        public void Dispose()
        {
            for (var i = objects.Count - 1; i >= 0; i--)
                if (Marshal.IsComObject(objects[i]))
                    Marshal.ReleaseComObject(objects[i]);
        }
    }

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupGetMembers(
        string? server,
        string group,
        int level,
        out IntPtr buffer,
        int preferredLength,
        out int entriesRead,
        out int totalEntries,
        ref nint resume);

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupAddMembers(
        string? server,
        string group,
        int level,
        ref IntPtr buffer,
        int count);

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupDelMembers(
        string? server,
        string group,
        int level,
        ref IntPtr buffer,
        int count);

    [DllImport("Netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}