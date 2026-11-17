using System.Text;
using System.Xml;
using System.Xml.Linq;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Imager.Api;
using CloneDeploy_Imager.Diagnostics;

namespace CloneDeploy_Imager.Deployment;

internal static class SysprepDomainJoin
{
    private const int MaximumAnswerFileCharacters = 1024 * 1024;

    private static readonly XNamespace Ns = "urn:schemas-microsoft-com:unattend";

    public static async Task ApplyAsync(string root, DeploymentExecution image,
        Func<ImagingOuRequest, CancellationToken, Task<string?>> resolve, DiagnosticLog log, CancellationToken ct)
    {
        if (!image.ConfigureDomainJoinOu) return;
        var files = new List<(string Path, XDocument Document, Encoding Encoding, List<XElement> Joins, string Domain, string Name)>();
        foreach (var relative in SysprepCredentials.AnswerPaths)
        {
            var path = Path.Combine(root, relative);
            if (!File.Exists(path)) continue;
            using var text = new StreamReader(path, new UTF8Encoding(false, true), true);
            var content = await text.ReadToEndAsync(ct);
            using var xml = XmlReader.Create(new StringReader(content), new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumAnswerFileCharacters });
            var document = XDocument.Load(xml, LoadOptions.PreserveWhitespace);
            var settings = document.Root?.Elements(Ns + "settings").Where(x => (string?)x.Attribute("pass") == "specialize").ToList() ?? [];
            var joins = settings.SelectMany(x => x.Elements(Ns + "component"))
                .Where(x => (string?)x.Attribute("name") == "Microsoft-Windows-UnattendedJoin")
                .SelectMany(x => x.Elements(Ns + "Identification"))
                .Where(x => !string.IsNullOrWhiteSpace((string?)x.Element(Ns + "JoinDomain"))).ToList();
            if (joins.Count == 0) continue;
            var domains = joins.Select(x => x.Element(Ns + "JoinDomain")!.Value.Trim().TrimEnd('.'))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (domains.Count != 1) throw new InvalidDataException("The answer file contains conflicting domain joins.");
            var names = settings.SelectMany(x => x.Elements(Ns + "component"))
                .Where(x => (string?)x.Attribute("name") == "Microsoft-Windows-Shell-Setup")
                .SelectMany(x => x.Elements(Ns + "ComputerName")).Select(x => x.Value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var name = image.SetComputerName ? image.ComputerName : names.Count == 1 ? names[0] : "";
            if (ComputerNameValidation.Error(name) is not null)
                throw new InvalidDataException("OU naming requires a known final Windows computer name. Enable 'Set computer name to device name' or use a fixed ComputerName in the answer file.");
            files.Add((path, document, text.CurrentEncoding, joins, domains[0], name));
        }
        if (files.Count == 0) return; // No answer file/domain join: never contact the server or prompt.
        if (files.Select(x => (x.Domain.ToUpperInvariant(), x.Name.ToUpperInvariant())).Distinct().Count() != 1)
            throw new InvalidDataException("Deployed answer files disagree on the domain or computer name. OU selection was stopped.");
        // Resolve and prompt once for every copy, before changing any answer file.
        var selected = await resolve(new(files[0].Domain, files[0].Name), ct);
        foreach (var file in files)
        {
            foreach (var join in file.Joins)
            {
                join.Elements(Ns + "MachineObjectOU").Remove();
                if (selected is not null) join.Add(new XElement(Ns + "MachineObjectOU", selected));
            }
            var temporary = file.Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = File.Create(temporary))
                using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Async = true, Encoding = file.Encoding, Indent = false }))
                    await file.Document.SaveAsync(writer, ct);
                ct.ThrowIfCancellationRequested();
                File.Replace(temporary, file.Path, null);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        log.Write(selected is null ? "Sysprep domain join will use domain default / existing account location."
            : "Sysprep domain join OU: " + selected);
    }
}
