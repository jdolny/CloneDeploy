namespace CloneDeploy_Server.Backend.Services.Files;

// Copy across volumes first, then rename within the destination directory. Keep the source
// until its owner cleans up; restore the previous destination if the database commit fails.
/// <summary>
/// Makes completed files available while protecting against incomplete replacements.
/// </summary>
public sealed class FilePublication : IDisposable
{
    private readonly string _destination;
    private readonly string _backup;
    private bool _committed;

    private FilePublication(string destination, string backup)
    {
        _destination = destination;
        _backup = backup;
    }

    public static async Task<FilePublication> CreateAsync(string source, string destination, bool replace = false, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var staging = destination + $".{Guid.NewGuid():N}.partial";
        var backup = destination + $".{Guid.NewGuid():N}.backup";
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
            await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                await input.CopyToAsync(output, ct);
                await output.FlushAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            if (replace && File.Exists(destination)) File.Move(destination, backup);
            try { File.Move(staging, destination); }
            catch
            {
                if (File.Exists(backup)) File.Move(backup, destination);
                throw;
            }
            return new FilePublication(destination, backup);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    public void Commit()
    {
        _committed = true;
        // A cleanup failure must not roll back a successfully committed database record.
        try { File.Delete(_backup); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        if (_committed) return;
        File.Delete(_destination);
        if (File.Exists(_backup)) File.Move(_backup, _destination);
    }
}
