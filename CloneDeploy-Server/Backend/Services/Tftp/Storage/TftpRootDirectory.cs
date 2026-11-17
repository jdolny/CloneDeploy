namespace CloneDeploy_Server.Backend.Services;

/// <summary>The fixed serving directory, shared by the listener and future boot-file generators.</summary>
public sealed class TftpRootDirectory(IHostEnvironment environment)
{
    public string RootPath { get; } = new ServerDataPaths(environment).Tftp;
    public string StagingPath { get; } = new ServerDataPaths(environment).NativePxeStaging;
    public string WinPePath => Path.Combine(RootPath, "WinPe");

    public void EnsureExists()
    {
        EnsureDirectory(RootPath);
        EnsureDirectory(WinPePath);
    }

    private static void EnsureDirectory(string path)
    {
        // Check each ancestor before creating children: never create files through a junction.
        if (Path.GetDirectoryName(path) is { } parent) EnsureDirectory(parent);
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("The TFTP directory and its ancestors must not be symbolic links or junctions.");
        }
        catch (FileNotFoundException) { Directory.CreateDirectory(path); }
        catch (DirectoryNotFoundException) { Directory.CreateDirectory(path); }
        if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != FileAttributes.Directory)
            throw new InvalidOperationException("The TFTP path must be a directory without symbolic links or junctions.");
    }
}
