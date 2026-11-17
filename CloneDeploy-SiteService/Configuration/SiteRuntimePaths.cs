namespace CloneDeploy.SiteService;

public sealed record SiteRuntimePaths(string DataRoot, string ConfigurationFile)
{
    public const string LinuxConfigurationFile = "/etc/clonedeploy/siteservice.json";
    public const string DefaultLinuxDataRoot = "/var/lib/clonedeploy/siteservice";

    public static SiteRuntimePaths Resolve()
    {
        var development = string.Equals(Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ??
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase);
        if (development)
        {
            var directory = Path.GetFullPath(Environment.GetEnvironmentVariable("CLONEDEPLOY_SITE_DATA") ?? SiteLocalStore.DefaultDirectory);
            return new(directory, Path.Combine(directory, "siteservice.json"));
        }
        if (OperatingSystem.IsLinux())
        {
            using var configuration = new ConfigurationManager();
            configuration.AddJsonFile(LinuxConfigurationFile, optional: false, reloadOnChange: false);
            var directory = ReadLinuxDataRoot(configuration, AppContext.BaseDirectory);
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException($"Create Site Service data directory '{directory}' and grant the service account ownership before importing enrollment or starting the service.");
            return new(directory, LinuxConfigurationFile);
        }
        return new(SiteLocalStore.DefaultDirectory, Path.Combine(SiteLocalStore.DefaultDirectory, "siteservice.json"));
    }

    public static string ReadLinuxDataRoot(IConfiguration configuration, string applicationDirectory)
    {
        var root = configuration["LinuxDataRoot"]?.Trim();
        if (string.IsNullOrEmpty(root)) root = DefaultLinuxDataRoot;
        root = root.TrimEnd('/');
        if (!root.StartsWith('/') || root.StartsWith("//") || root.Contains('\\') ||
            root.Any(char.IsControl) || root.Split('/').Skip(1).Any(part => part is "" or "." or ".."))
            throw new InvalidOperationException("LinuxDataRoot in /etc/clonedeploy/siteservice.json must be an absolute Linux directory, not / or a relative path.");
        var application = applicationDirectory.TrimEnd('/');
        if (root == application || root.StartsWith(application + "/", StringComparison.Ordinal) ||
            application.StartsWith(root + "/", StringComparison.Ordinal))
            throw new InvalidOperationException("LinuxDataRoot must be separate from the application publish directory.");
        return root;
    }
}
