using System.Xml;
using System.Xml.Linq;

namespace CloneDeploy_Agent.Contracts;

public sealed record AgentImagePrepProfile(int Id, string Name, string? Description)
{
    public bool RebootAndCapture { get; init; }
    public bool UninstallAgent { get; init; }
}

public sealed record AgentImagePrepPlan(
    int Id, string Name, string? AnswerFile, string? SetupComplete, string? PreSysprepScript,
    bool DisableHibernate, bool DisableBitLocker, bool ResetAgent, bool RunSysprep)
{
    public bool RebootAndCapture { get; init; }
    public AgentPreparedCapture? Capture { get; init; }
    public string? SysprepCredentialTicket { get; init; }
    public bool UninstallAgent { get; init; }
}

public enum ImagePrepAgentMode { Keep, Reset, Uninstall }

public enum AgentImagePrepOperation { Profiles, Start, Status }

public sealed record AgentImagePrepRequest(AgentImagePrepOperation Operation, int ProfileId = 0)
{
    public int? ImageId { get; init; }
    public string? NewImageName { get; init; }
    public string? Format { get; init; }
    public string? NativeArchitecture { get; init; }
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
}

public sealed record AgentImagePrepStatus(string Status, string Message, int Progress = 0)
{
    public AgentImagePrepProfile[] Profiles { get; init; } = [];
    public AgentCaptureImage[] Images { get; init; } = [];
}

public sealed record AgentCaptureImage(int Id, string Name, string Format)
{
    public string DisplayName => $"{Name} ({Format.ToUpperInvariant()})";
}

public sealed record AgentPreparedCapture(Guid UploadId, string Token, DateTime ExpiresAtUtc,
    AgentImagingReview Boot)
{
    public string Format { get; init; } = "Wim";
    public override string ToString() => "Prepared capture (token redacted)";
}

public static class ImagePrepAnswerFile
{
    public const int MaximumContentLength = 128 * 1024;

    public static string? Validate(string? text)
    {
        if (text?.Length > MaximumContentLength)
            return "Answer files are limited to 128K characters.";

        if (string.IsNullOrWhiteSpace(text))
            return null;

        try
        {
            using var input = new StringReader(text);
            using var reader = XmlReader.Create(input, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumContentLength
            });
            var document = XDocument.Load(reader);
            if (document.Root?.Name != XName.Get("unattend", "urn:schemas-microsoft-com:unattend"))
                return "Use a Windows answer file with an unattend root and the Windows unattend namespace.";
        }
        catch (XmlException)
        {
            return "The answer file must contain valid XML without a DTD.";
        }

        return null;
    }

    public static string GenerateBasic() => """
        <?xml version="1.0" encoding="utf-8"?>
        <unattend xmlns="urn:schemas-microsoft-com:unattend">
          <settings pass="specialize">
            <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
              <ComputerName>*</ComputerName>
            </component>
          </settings>
          <settings pass="oobeSystem">
            <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
              <OOBE>
                <HideOEMRegistrationScreen>true</HideOEMRegistrationScreen>
              </OOBE>
            </component>
          </settings>
        </unattend>
        """;
}
