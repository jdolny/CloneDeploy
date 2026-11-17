namespace CloneDeploy_Server.Backend.Services.Files;

/// <summary>
/// Describes an upload problem and how it should be reported to the caller.
/// </summary>
public sealed class UploadException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
