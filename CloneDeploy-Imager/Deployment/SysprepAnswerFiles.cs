using System.Text;
using System.Xml;
using CloneDeploy_Imager.Api;
using CloneDeploy_Imager.Diagnostics;

namespace CloneDeploy_Imager.Deployment;

internal static class SysprepAnswerFiles
{
    public static async Task ApplyAsync(string windowsRoot, DeploymentExecution image, DiagnosticLog log, CancellationToken ct)
    {
        if (!image.SetComputerName) return;
        if (image.SetComputerName) OfflineComputerName.Validate(image.ComputerName);

        var found = false;
        foreach (var relative in SysprepCredentials.AnswerPaths)
        {
            var path = Path.Combine(windowsRoot, relative);
            if (!File.Exists(path)) continue;
            found = true;

            string original;
            Encoding encoding;
            using (var reader = new StreamReader(path, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true))
            {
                original = await reader.ReadToEndAsync(ct);
                encoding = reader.CurrentEncoding;
            }

            var modified = original;
            if (image.SetComputerName)
            {
                var document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
                using (var xml = XmlReader.Create(new StringReader(original), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                    document.Load(xml);
                var names = document.SelectNodes("//*[local-name()='ComputerName']")!;
                foreach (XmlNode node in names) node.InnerText = image.ComputerName;
                if (names.Count > 0) modified = document.OuterXml;
                else log.Write($"WARNING: No ComputerName element found in {relative}; the offline registry name was still set.");
            }
            if (modified == original) continue;
            using (var xml = XmlReader.Create(new StringReader(modified), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                while (xml.Read()) { }

            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, modified, encoding, ct);
                ct.ThrowIfCancellationRequested();
                File.Replace(temporary, path, null);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            log.Write($"Updated computer name in {relative}.");
        }
        if (!found) log.Write("No Sysprep answer file found; continuing without answer-file modifications.");
    }
}
