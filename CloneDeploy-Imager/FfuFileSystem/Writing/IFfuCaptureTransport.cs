namespace CloneDeploy_Imager.FfuFileSystem.Writing;

/// <summary>
/// Defines remote capture operations used by the upload buffer, including ordered batches of writes.
/// </summary>
internal interface IFfuCaptureTransport
{
    bool Exists { get; }
    long Length { get; }
    void Create();
    void SetLength(long length);
    void WriteBatch(byte[] data);
    byte[] Read(long offset, int count);
    void Flush();
}
