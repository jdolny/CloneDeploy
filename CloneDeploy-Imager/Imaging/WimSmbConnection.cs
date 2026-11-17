using System.Text.RegularExpressions;
using CloneDeploy_Imager.Api;

namespace CloneDeploy_Imager.Imaging;

internal static class WimSmbConnection
{
    public static void Validate(SmbCapture direct)
    {
        SmbCaptureConnection.Validate(direct);
        if (string.IsNullOrWhiteSpace(direct.Username) || direct.Password is null)
            throw new InvalidDataException("The server did not provide managed SMB credentials.");
    }

    public static void ValidateDeployment(DeploymentExecution image)
    {
        if (image.Format != "Wim" || image.Direct is not { } direct)
            throw new InvalidDataException("Missing WIM SMB connection.");
        Validate(direct);
        if (!Regex.IsMatch(direct.RelativePath, @"\AImages\\[1-9][0-9]*\\[0-9a-fA-F]{32}\\image\.wim\z", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Invalid published WIM path.");
    }

    public static (string Path, IDisposable Connection) Connect(SmbCapture direct)
    {
        Validate(direct);
        var connection = SmbCaptureConnection.Connect(direct, direct.Username!, direct.Password!);
        return (connection.FilePath, connection);
    }
}
