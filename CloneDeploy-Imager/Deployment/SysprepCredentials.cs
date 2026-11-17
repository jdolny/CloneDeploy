using System.Text;
using System.Xml;
using System.Xml.Linq;
using CloneDeploy_Agent.Contracts;
using CloneDeploy_Imager.Diagnostics;

namespace CloneDeploy_Imager.Deployment;

internal static class SysprepCredentials
{
    private const int MaximumAnswerFileCharacters = 128 * 1024;
    private const int MaximumCredentialTicketBytes = 32768;

    internal static readonly string[] AnswerPaths =
    [
        @"Windows\Panther\unattend.xml"
    ];
    private static readonly XNamespace Ns = "urn:schemas-microsoft-com:unattend";
    public static async Task ApplyAsync(string root, Func<string, CancellationToken, Task<Dictionary<string, string>>>? resolve,
        DiagnosticLog log, CancellationToken ct)
    {
        var ticketPath = Path.Combine(root, ImagePrepCredentialFiles.TicketRelativePath);
        var documents = new List<(string Path, XDocument Document)>();
        foreach (var relative in AnswerPaths)
        {
            var path = Path.Combine(root, relative);
            if (!File.Exists(path)) continue;
            var content = await File.ReadAllTextAsync(path, ct);
            if (!content.Contains(ImagePrepCredentialFiles.MarkerPrefix, StringComparison.Ordinal)) continue;
            using var reader = XmlReader.Create(new StringReader(content), new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumAnswerFileCharacters });
            var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            documents.Add((path, document));
        }
        if (documents.Count == 0)
        {
            if (File.Exists(ticketPath)) throw new InvalidDataException("Managed Sysprep credentials were staged but their answer-file placeholders are missing.");
            return;
        }
        if (resolve is null || !File.Exists(ticketPath))
            throw new InvalidDataException("The image has managed-password placeholders without deployment authorization. Prepare and capture it with a current agent.");
        if (new FileInfo(ticketPath).Length > MaximumCredentialTicketBytes) throw new InvalidDataException("Invalid Sysprep credential authorization.");
        var values = await resolve(await File.ReadAllTextAsync(ticketPath, ct), ct);
        if (values.Count == 0 || values.Any(x => !x.Key.StartsWith(ImagePrepCredentialFiles.MarkerPrefix, StringComparison.Ordinal) || string.IsNullOrEmpty(x.Value)))
            throw new InvalidDataException("Incomplete Sysprep credential response.");
        foreach (var (_, document) in documents)
        {
            var placeholders = document.Descendants().Where(x => !x.HasElements && x.Value.StartsWith(ImagePrepCredentialFiles.MarkerPrefix, StringComparison.Ordinal)).ToList();
            if (placeholders.Count == 0) throw new InvalidDataException("Invalid Sysprep credential placeholders.");
            foreach (var element in placeholders)
            {
                if (!IsPassword(element) || !values.TryGetValue(element.Value, out var password))
                    throw new InvalidDataException("An answer-file credential placeholder is invalid or unauthorized.");
                element.Value = password; // XML serialization escapes &, <, quotes, and other special characters.
            }
        }
        foreach (var (path, document) in documents)
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, document.ToString(), new UTF8Encoding(false), ct);
                File.Replace(temporary, path, null);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        File.Delete(ticketPath);
        log.Write("Inserted managed Sysprep passwords.");
    }

    private static bool IsPassword(XElement element) => element.Name == Ns + "Password" && element.Parent?.Name == Ns + "Credentials"
        || element.Name == Ns + "Value" && (element.Parent?.Name == Ns + "Password" || element.Parent?.Name == Ns + "AdministratorPassword");
}
