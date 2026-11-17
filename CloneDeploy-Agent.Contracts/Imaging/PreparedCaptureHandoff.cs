namespace CloneDeploy_Agent.Contracts;

// Kept on the source Windows volume, consumed and removed by WinPE before capture.
public sealed record PreparedCaptureHandoff(Guid UploadId, string Token, string ServerUrl, int ImageId,
    string ImageName, string Format, DateTime ExpiresAtUtc)
{
    public const string DirectoryName = "CloneDeployCapture";
    public const string FileName = "capture.json";
    public override string ToString() => "Capture handoff (token redacted)";
}
