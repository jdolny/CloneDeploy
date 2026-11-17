using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>Cross-platform, unsigned development installer: XAR containing PackageInfo and gzip/odc scripts.
/// The script-only package embeds the existing archive, preserving the agent's install and upgrade logic.</summary>
internal static class MacInstallerPackage
{
    private const string PostInstall = """
        #!/bin/bash
        set -euo pipefail
        export PATH=/usr/bin:/bin:/usr/sbin:/sbin
        umask 077
        if [[ "${3:-/}" != / || "$(uname -s)" != Darwin || "$(uname -m)" != arm64 ]]; then
            echo "CloneDeploy requires the running Apple Silicon macOS system volume." >&2; exit 1
        fi
        scripts="$(cd -- "$(dirname -- "$0")" && pwd -P)"
        staging=$(mktemp -d /private/tmp/clonedeploy-install.XXXXXX)
        cleanup() {
            case "$staging" in /private/tmp/clonedeploy-install.*) rm -rf -- "$staging" ;; esac
        }
        trap cleanup EXIT
        tar -xzf "$scripts/agent.tar.gz" -C "$staging"
        root="/Library/Application Support/CloneDeploy/Agent"
        pending="$root/EnrollmentPending"
        used_pending=0
        # The original PKG and its adjacent enrollment file remain outside the signed package.
        package="${PACKAGE_PATH:-${1:-}}"
        if [[ -n "$package" && "$package" == /* && -f "$package" ]]; then
            enrollment="$(dirname -- "$package")/enrollment.json"
            if [[ -e "$enrollment" || -L "$enrollment" ]]; then
                [[ -f "$enrollment" && ! -L "$enrollment" ]] || { echo 'Invalid adjacent enrollment file.' >&2; exit 1; }
                [[ "$(stat -f '%z' "$enrollment")" -le 1048576 ]] || { echo 'Enrollment file is too large.' >&2; exit 1; }
                install -o root -g wheel -m 600 "$enrollment" "$staging/enrollment.json"
            fi
        fi
        # MDM staging is only for first installation. Never replace saved settings on an update.
        if [[ ! -f "$staging/enrollment.json" && ! -e "$root/configuration.json" && ! -L "$root/configuration.json" ]]; then
            for path in /Library "/Library/Application Support" "/Library/Application Support/CloneDeploy" "$root" "$pending" "$pending/enrollment.json"; do
                [[ ! -L "$path" ]] || { echo 'Refusing redirected enrollment path.' >&2; exit 1; }
            done
            [[ -f "$pending/enrollment.json" ]] || {
                echo "Initial enrollment requires enrollment.json beside the PKG or at $pending/enrollment.json." >&2; exit 1;
            }
            for path in "/Library/Application Support/CloneDeploy" "$root" "$pending" "$pending/enrollment.json"; do
                [[ "$(stat -f '%u' "$path")" == 0 ]] || { echo 'Enrollment staging must be owned by root.' >&2; exit 1; }
                mode=$(stat -f '%Lp' "$path")
                (( (8#$mode & 0022) == 0 )) || { echo 'Enrollment staging must not be writable by other users.' >&2; exit 1; }
            done
            [[ "$(stat -f '%z' "$pending/enrollment.json")" -le 1048576 ]] || { echo 'Enrollment file is too large.' >&2; exit 1; }
            install -o root -g wheel -m 600 "$pending/enrollment.json" "$staging/enrollment.json"
            used_pending=1
        fi
        /bin/bash "$staging/install.sh"
        # Keep the MDM input on failure; consume it only after installation succeeds.
        if [[ "$used_pending" == 1 ]]; then
            rm -f -- "$pending/enrollment.json"
            rmdir -- "$pending" || echo "Enrollment staging directory is not empty; remove leftover files before automatic updates." >&2
        fi
        """;

    public static async Task CreateAsync(string archive, string destination, string version, CancellationToken ct)
    {
        var parsed = Version.Parse(version);
        var scriptsPath = destination + ".scripts";
        try
        {
            await using (var file = new FileStream(scriptsPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
            {
                await CpioEntry(gzip, ".", 0x41C0, Stream.Null, 0, 1, ct); // directory 0700
                using var script = new MemoryStream(Encoding.UTF8.GetBytes(PostInstall.Replace("\r\n", "\n") + "\n"));
                await CpioEntry(gzip, "./postinstall", 0x81C0, script, script.Length, 2, ct); // file 0700
                await using var input = File.OpenRead(archive);
                await CpioEntry(gzip, "./agent.tar.gz", 0x8180, input, input.Length, 3, ct); // file 0600
                await CpioEntry(gzip, "TRAILER!!!", 0, Stream.Null, 0, 0, ct);
            }
            var info = new XDocument(new XElement("pkg-info",
                new XAttribute("format-version", "2"), new XAttribute("identifier", "com.clonedeploy.agent"),
                new XAttribute("version", $"{parsed.Major}.{parsed.Minor}.{Math.Max(0, parsed.Build)}"),
                new XAttribute("install-location", "/"), new XAttribute("auth", "root"),
                new XAttribute("relocatable", "false"), new XAttribute("overwrite-permissions", "false"),
                new XAttribute("postinstall-action", "none"),
                new XElement("payload", new XAttribute("numberOfFiles", "0"), new XAttribute("installKBytes", "0")),
                new XElement("scripts", new XElement("postinstall", new XAttribute("file", "./postinstall"), new XAttribute("timeout", "600")))));
            using var manifest = new MemoryStream(Encoding.UTF8.GetBytes(info.ToString()));
            await using var scripts = File.OpenRead(scriptsPath);
            await WriteXar(destination, [("PackageInfo", manifest), ("Scripts", scripts)], ct);
        }
        finally { if (File.Exists(scriptsPath)) File.Delete(scriptsPath); }
    }

    // Portable ASCII (odc) cpio uses fixed-width octal fields and no entry padding.
    private static async Task CpioEntry(Stream output, string name, int mode, Stream data, long length, int inode, CancellationToken ct)
    {
        var path = Encoding.UTF8.GetBytes(name + "\0");
        static string Octal(long value, int width)
        {
            var text = Convert.ToString(value, 8);
            if (value < 0 || text.Length > width) throw new InvalidDataException("Mac package entry exceeds the cpio format limit.");
            return text.PadLeft(width, '0');
        }
        var header = "070707" + Octal(0, 6) + Octal(inode, 6) + Octal(mode, 6) + Octal(0, 6) + Octal(0, 6)
            + Octal(1, 6) + Octal(0, 6) + Octal(0, 11) + Octal(path.Length, 6) + Octal(length, 11);
        await output.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
        await output.WriteAsync(path, ct);
        await data.CopyToAsync(output, ct);
    }

    private static async Task WriteXar(string destination, (string Name, Stream Data)[] entries, CancellationToken ct)
    {
        // SHA-1 is the interoperable XAR format checksum, not an authenticity/signing mechanism.
        var toc = new XElement("toc", new XElement("checksum", new XAttribute("style", "sha1"),
            new XElement("offset", 0), new XElement("size", 20)));
        long offset = 20;
        var id = 0;
        foreach (var (name, data) in entries)
        {
            var digest = Convert.ToHexString(await SHA1.HashDataAsync(data, ct)).ToLowerInvariant();
            data.Position = 0;
            toc.Add(new XElement("file", new XAttribute("id", ++id), new XElement("name", name),
                new XElement("type", "file"), new XElement("mode", "0600"), new XElement("uid", 0),
                new XElement("gid", 0), new XElement("user", "root"), new XElement("group", "wheel"),
                new XElement("data", new XElement("length", data.Length), new XElement("offset", offset),
                    new XElement("size", data.Length), new XElement("encoding", new XAttribute("style", "application/octet-stream")),
                    new XElement("archived-checksum", new XAttribute("style", "sha1"), digest),
                    new XElement("extracted-checksum", new XAttribute("style", "sha1"), digest))));
            offset = checked(offset + data.Length);
        }
        var xml = Encoding.UTF8.GetBytes(new XDocument(new XElement("xar", toc)).ToString());
        using var compressed = new MemoryStream();
        await using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            await zlib.WriteAsync(xml, ct);
        var bytes = compressed.ToArray();
        var header = new byte[28];
        "xar!"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), 28);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), 1);
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(8), bytes.Length);
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(16), xml.Length);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(24), 1);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        await output.WriteAsync(header, ct);
        await output.WriteAsync(bytes, ct);
        await output.WriteAsync(SHA1.HashData(bytes), ct);
        foreach (var entry in entries) await entry.Data.CopyToAsync(output, ct);
    }
}
