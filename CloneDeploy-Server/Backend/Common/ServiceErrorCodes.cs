namespace CloneDeploy_Server.Backend.Common;

// Transport-independent outcomes; API controllers choose HTTP status codes and response shapes.
public static class ServiceErrorCodes
{
    public const string Invalid = "Request.Invalid";
    public const string Forbidden = "Request.Forbidden";
    public const string Unauthorized = "Request.Unauthorized";
    public const string NotFound = "Request.NotFound";
    public const string Conflict = "Request.Conflict";
    public const string Busy = "Request.Busy";
    public const string Unavailable = "Request.Unavailable";
    public const string Failed = "Request.Failed";
}
