using CloneDeploy_Imager.Api;

namespace CloneDeploy_Imager.FfuFileSystem.Writing;

// Callbacks are serialized by the WinFsp host. No write is acknowledged locally
// until the server returns its matching sequence receipt.
/// <summary>
/// Translates capture-file operations into sequenced HTTPS requests and tracks acknowledged file state.
/// </summary>
internal sealed class HttpsFfuCaptureFile(ImagingClient client, Guid id, CancellationToken ct) : IFfuCaptureTransport
{
    private long sequence;
    public bool Exists { get; private set; }
    public long Length { get; private set; }
    private void Mutate(string operation, long offset, byte[] bytes)
    {
        var state = client.MutateFfuAsync(id, sequence + 1, operation, offset, bytes, ct).GetAwaiter().GetResult();
        sequence = state.Sequence; Exists = state.Exists; Length = state.Length;
    }
    public void Create() => Mutate("create", 0, []);
    public void SetLength(long length) => Mutate("length", length, []);
    public void WriteBatch(byte[] data) => Mutate("batch", 0, data);
    public byte[] Read(long offset, int count) => client.ReadFfuUploadAsync(id, offset, count, ct).GetAwaiter().GetResult();
    public void Flush() { if (Exists) Mutate("flush", 0, []); }
}
