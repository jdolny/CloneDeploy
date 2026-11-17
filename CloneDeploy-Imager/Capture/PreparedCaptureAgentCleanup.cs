using System.Text.Json;

namespace CloneDeploy_Imager;

internal static class PreparedCaptureAgentCleanup
{
    internal static void Clear(string windowsRoot, Guid uploadId)
    {
        var state = Path.Combine(windowsRoot, "ProgramData", "CloneDeploy", "Agent");
        var journal = Path.Combine(state, "WinPe", "pending.json");
        var files = new List<string> { journal };
        foreach (var name in new[] { "agent.log", "agent.1.log", "agent.2.log", "agent.3.log", "agent.4.log",
                     "image-prep-finalization.log", "image-prep-uninstall.log" })
        {
            files.Add(Path.Combine(state + "Logs", name));
            files.Add(name.StartsWith("agent.", StringComparison.Ordinal)
                ? Path.Combine(state, "logs", name) : Path.Combine(state, name));
        }
        // Validate every path before deleting anything. Never follow links out of the source volume.
        foreach (var file in files)
            for (var current = Path.GetFullPath(file); current is not null; current = Path.GetDirectoryName(current))
                if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Prepared capture agent cleanup cannot follow file or directory links.");
        if (File.Exists(journal))
        {
            if (new FileInfo(journal).Length > 16384) throw new InvalidDataException("Capture boot journal is too large.");
            using var pending = JsonDocument.Parse(File.ReadAllText(journal));
            if (!pending.RootElement.TryGetProperty("CaptureUploadId", out var captureId)
                || !captureId.TryGetGuid(out var id) || id != uploadId)
                throw new InvalidDataException("Agent boot journal does not belong to this prepared capture.");
        }
        foreach (var file in files)
            if (Path.Exists(file)) File.Delete(file);
    }
}
