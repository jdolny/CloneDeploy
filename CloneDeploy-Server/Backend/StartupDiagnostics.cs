using System.Text;

namespace CloneDeploy_Server.Backend;

/// <summary>Last-resort diagnostics that do not depend on configuration or host construction.</summary>
public static class StartupDiagnostics
{
    public static string? WriteFailure(Exception exception, string? dataDirectory = null, string? temporaryDirectory = null)
    {
        var entry = $"[{DateTimeOffset.UtcNow:O}] Application terminated unexpectedly{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}";
        try { Console.Error.Write(entry); } catch { /* A service may have no stderr handle. */ }

        // Never create a missing installation data directory: that can hide a missing data-volume mount.
        try
        {
            var root = dataDirectory ?? Directory.GetCurrentDirectory();
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Server data directory is unavailable.");
            return Append(Path.Combine(root, "logs"), entry);
        }
        catch (Exception error) { ReportUnavailable(error); }

        try
        {
            return Append(Path.Combine(temporaryDirectory ?? Path.GetTempPath(), "CloneDeploy-Server", "logs"), entry);
        }
        catch (Exception error) { ReportUnavailable(error); return null; }
    }

    private static string Append(string directory, string entry)
    {
        Directory.CreateDirectory(directory);
        var path = Path.GetFullPath(Path.Combine(directory, "startup-errors.log"));
        File.AppendAllText(path, entry, Encoding.UTF8);
        try { Console.Error.WriteLine($"Startup error details: {path}"); } catch { }
        return path;
    }

    private static void ReportUnavailable(Exception exception)
    {
        try { Console.Error.WriteLine($"Could not write startup error log: {exception.Message}"); } catch { }
    }
}
