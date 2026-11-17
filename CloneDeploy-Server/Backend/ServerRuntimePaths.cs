namespace CloneDeploy_Server.Backend;

/// <summary>Resolved once before host creation; configuration and writable data may live separately.</summary>
public sealed record ServerRuntimePaths(string DataRoot, string ConfigurationFile)
{
    public const string LinuxConfigurationFile = "/etc/clonedeploy/server.json";
    public const string DefaultLinuxDataRoot = "/var/lib/clonedeploy/server";

    public static string WindowsDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CloneDeploy", "Server");

    public static string ReadLinuxDataRoot(IConfiguration configuration, string applicationDirectory)
    {
        var root = configuration["LinuxDataRoot"]?.Trim();
        if (string.IsNullOrEmpty(root)) root = DefaultLinuxDataRoot;
        root = root.TrimEnd('/');
        if (!root.StartsWith('/') || root.StartsWith("//") || root.Contains('\\') ||
            root.Any(char.IsControl) || root.Split('/').Skip(1).Any(part => part is "" or "." or ".."))
            throw new InvalidOperationException("LinuxDataRoot in /etc/clonedeploy/server.json must be an absolute Linux directory, not / or a relative path.");
        var application = applicationDirectory.TrimEnd('/');
        if (root == application || root.StartsWith(application + "/", StringComparison.Ordinal) ||
            application.StartsWith(root + "/", StringComparison.Ordinal))
            throw new InvalidOperationException("LinuxDataRoot must be separate from the application publish directory.");
        return root;
    }
}
