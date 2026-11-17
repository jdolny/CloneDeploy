using System.Xml;
using System.Xml.Linq;

namespace CloneDeploy_Imager.Deployment;

internal static class SysprepSourceCleanup
{
    internal static void Apply(string root, Action<string> log)
    {
        var sources = new[] { "unattend.xml", "autounattend.xml" }
            .SelectMany(name => new[] { name, name + ".clonedeploy-backup" })
            .Select(name => Path.Combine(root, @"Windows\System32\Sysprep", name)).ToArray();
        var cached = Path.Combine(root, @"Windows\Panther\unattend.xml");
        foreach (var path in sources.Append(cached))
            for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
                if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Answer-file cleanup cannot follow file or directory links.");
        if (!sources.Any(File.Exists)) return;
        if (!File.Exists(cached))
            throw new InvalidDataException("Cannot remove the Sysprep answer file: Windows\\Panther\\unattend.xml is missing. Generalize the image before capture.");
        using (var reader = XmlReader.Create(cached, new XmlReaderSettings
               { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 128 * 1024 }))
        {
            var document = XDocument.Load(reader);
            if (document.Root?.Name != XName.Get("unattend", "urn:schemas-microsoft-com:unattend"))
                throw new InvalidDataException("The Panther answer file is not a valid unattend document.");
        }
        foreach (var path in sources)
        {
            if (!File.Exists(path)) continue;
            File.Delete(path);
            log($"Removed original Sysprep answer file: {Path.GetFileName(path)}; retained the updated Panther answer file.");
        }
    }
}
