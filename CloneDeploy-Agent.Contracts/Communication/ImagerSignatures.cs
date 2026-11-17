using System.Text.Json;

namespace CloneDeploy_Agent.Contracts;

/// <summary>Response binding for token-authenticated Imagers, separate from enrolled agents.</summary>
public static class ImagerSignatures
{
    public const string ProtocolHeader = "X-CloneDeploy-Imager-Signing";
    public const string Version = "1";
    public const string BootstrapVersion = "bootstrap-1";
    public static string BootstrapContext(string nonce, string path) =>
        ServerSignatures.Hash(System.Text.Encoding.UTF8.GetBytes("CloneDeploy/imager-bootstrap/v1\n" + nonce + "\n" + path));
    public static readonly string[] BoundHeaders = ["Authorization", "Content-Type", "Range", "If-Match", "If-Range",
        "X-CloneDeploy-Capture", "X-Imager-OU"];

    public static string Context(string nonce, string method, string path, string bodyHash, Func<string, string[]> headers) =>
        ServerSignatures.Context(nonce, method, path,
            ServerSignatures.Hash(JsonSerializer.SerializeToUtf8Bytes(new { Body = bodyHash, Headers = BoundHeaders.Select(headers).ToArray() })),
            "CloneDeploy/imager/v1");
}
