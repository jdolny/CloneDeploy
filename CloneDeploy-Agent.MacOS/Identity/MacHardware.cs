using System.Diagnostics;
using System.Xml;
using System.Xml.Linq;
namespace CloneDeploy_Agent.MacOS;

internal static class MacHardware
{
    private const int MaximumProbeCharacters = 1024 * 1024;

    internal static async Task<string?> ReadUuidAsync(CancellationToken ct)
    {
        var start = new ProcessStartInfo("/usr/sbin/ioreg") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-rd1", "-c", "IOPlatformExpertDevice", "-a" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not read Mac hardware identity.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        var xml = await output; _ = await error;
        if (process.ExitCode != 0 || xml.Length > MaximumProbeCharacters) return null;
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = MaximumProbeCharacters });
        var document = XDocument.Load(reader);
        var value = document.Descendants("key").FirstOrDefault(x => x.Value == "IOPlatformUUID")?.ElementsAfterSelf("string").FirstOrDefault()?.Value;
        return Guid.TryParse(value, out var uuid) && uuid != Guid.Empty ? uuid.ToString() : null;
    }
}
