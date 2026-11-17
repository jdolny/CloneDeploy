using System.Formats.Tar;
using System.IO.Compression;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>Reads the upstream archive without ever creating filesystem links.</summary>
public static class PxeBootArchive
{
    private const long MaximumBytes = 128 * 1024 * 1024;

    public static async Task<Dictionary<string, byte[]>> ReadAsync(Stream compressed, CancellationToken ct)
    {
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress, leaveOpen: true);
        using var tar = new TarReader(gzip, leaveOpen: true);
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        while (await tar.GetNextEntryAsync(copyData: false, ct) is { } entry)
        {
            var name = entry.Name.TrimEnd('/');
            ValidatePath(name);
            if (name != "ipxeboot" && !name.StartsWith("ipxeboot/", StringComparison.Ordinal))
                throw new InvalidDataException("The iPXE archive contains an unexpected top-level path.");
            if (!entries.Add(name) || entries.Count > 4096) throw new InvalidDataException("Duplicate or excessive archive entries.");
            switch (entry.EntryType)
            {
                case TarEntryType.Directory: break;
                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                    total += entry.Length;
                    if (entry.Length < 0 || entry.Length > 32 * 1024 * 1024 || total > MaximumBytes)
                        throw new InvalidDataException("The iPXE archive is too large.");
                    var bytes = new byte[checked((int)entry.Length)];
                    if (entry.DataStream is not null) await entry.DataStream.ReadExactlyAsync(bytes, ct);
                    files.Add(name, bytes);
                    break;
                case TarEntryType.SymbolicLink:
                    ValidatePath(entry.LinkName);
                    var parent = name[..(name.LastIndexOf('/') + 1)];
                    aliases.Add(name, parent + entry.LinkName);
                    break;
                default: throw new InvalidDataException("Unsupported archive entry type.");
            }
        }
        // No file or alias may also be an ancestor of an explicitly listed entry.
        foreach (var name in files.Keys.Concat(aliases.Keys))
            if (entries.Any(other => other.StartsWith(name + "/", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Conflicting archive paths.");
        // Resolve aliases only to actual regular files/directories in this archive.
        // Directory aliases (e.g. sb -> x86_64-sb) become ordinary copied files.
        var originals = files.ToArray();
        foreach (var alias in aliases)
        {
            var target = alias.Value;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { alias.Key };
            while (aliases.TryGetValue(target, out var next))
            {
                if (!visited.Add(target)) throw new InvalidDataException("Cyclic archive alias.");
                target = next;
            }
            var matches = originals.Where(file => file.Key.Equals(target, StringComparison.OrdinalIgnoreCase) ||
                file.Key.StartsWith(target + "/", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0) throw new InvalidDataException("Archive alias has no regular-file target.");
            foreach (var file in matches)
            {
                var name = alias.Key + file.Key[target.Length..];
                if (!files.TryAdd(name, file.Value) || files.Count > 4096 || (total += file.Value.Length) > MaximumBytes)
                    throw new InvalidDataException("Conflicting or excessive archive aliases.");
            }
        }
        // Copy aliases nested beneath directory aliases too (the upstream Secure Boot shims).
        foreach (var alias in aliases.Where(x => !files.ContainsKey(x.Key)))
            foreach (var file in files.Where(x => x.Key.StartsWith(alias.Value + "/", StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                var name = alias.Key + file.Key[alias.Value.Length..];
                if (files.ContainsKey(name)) continue;
                if (files.Count >= 4096 || (total += file.Value.Length) > MaximumBytes) throw new InvalidDataException("Excessive archive aliases.");
                files.Add(name, file.Value);
            }
        foreach (var required in new[] { "ipxeboot/undionly.kpxe", "ipxeboot/i386/ipxe.efi", "ipxeboot/x86_64/ipxe.efi",
                     "ipxeboot/x86_64-sb/ipxe-shim.efi", "ipxeboot/x86_64-sb/ipxe.efi",
                     "ipxeboot/arm64/ipxe.efi", "ipxeboot/arm64-sb/ipxe-shim.efi", "ipxeboot/arm64-sb/ipxe.efi" })
            if (!files.TryGetValue(required, out var data) || data.Length == 0) throw new InvalidDataException("The release is missing required BIOS/UEFI boot files.");
        return files;
    }

    public static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 240 || path.Contains('\\') || path.Contains(':') || path.Any(char.IsControl))
            throw new InvalidDataException("Unsafe archive path.");
        foreach (var part in path.Split('/'))
        {
            var stem = part.Split('.')[0].ToUpperInvariant();
            if (part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0 ||
                stem is "CON" or "PRN" or "AUX" or "NUL" ||
                stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3]))
                throw new InvalidDataException("Unsafe archive path.");
        }
    }
}
