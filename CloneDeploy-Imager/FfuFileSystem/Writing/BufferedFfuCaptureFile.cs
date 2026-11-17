using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using CloneDeploy_Imager.Diagnostics;

namespace CloneDeploy_Imager.FfuFileSystem.Writing;

// DISM emits small compressed writes. Batch arbitrary offsets without reordering
// overwrites or allowing inspection/publication to observe unuploaded data.
/// <summary>
/// Batches ordered writes within a fixed memory limit and drains them before reads, resizing, or flush.
/// </summary>
internal sealed class BufferedFfuCaptureFile(IFfuCaptureTransport remote, DiagnosticLog log)
{
    private const int BatchBytes = 8 * 1024 * 1024;
    private const int RecordHeaderBytes = sizeof(long) + sizeof(int);
    private const int MaximumRecords = 65536;
    private readonly object gate = new();
    private readonly byte[] buffer = new byte[BatchBytes];
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private long length = remote.Length, writes, inputBytes, uploads, uploadedBytes, uploadTicks;
    private long fullDrains, readDrains, sizeDrains, flushDrains;
    private int pendingBytes;
    private int records;
    private int count;
    private TimeSpan lastReport;
    private ExceptionDispatchInfo? failure;
    public bool Exists { get { lock (gate) return remote.Exists; } }
    public long Length { get { lock (gate) return length; } }

    private void Run(Action action)
    {
        // Once a deferred write fails, later callbacks must not hide the failure
        // and allow final verification to publish an incomplete capture.
        failure?.Throw();
        try { action(); }
        catch (Exception ex)
        {
            failure = ExceptionDispatchInfo.Capture(ex);
            throw;
        }
    }

    private void Drain(string reason)
    {
        if (count == 0) return;
        var start = Stopwatch.GetTimestamp();
        remote.WriteBatch(count == buffer.Length ? buffer : buffer.AsSpan(0, count).ToArray());
        uploadTicks += Stopwatch.GetTimestamp() - start;
        uploads++;
        uploadedBytes += pendingBytes;
        count = 0;
        pendingBytes = 0;
        records = 0;
        switch (reason)
        {
            case "full": fullDrains++; break;
            case "read": readDrains++; break;
            case "size": sizeDrains++; break;
            case "flush": flushDrains++; break;
        }
        if (elapsed.Elapsed - lastReport >= TimeSpan.FromSeconds(30)) Report();
    }

    public void Create()
    {
        lock (gate) Run(() =>
        {
            remote.Create();
            length = remote.Length;
        });
    }
    public void SetLength(long value)
    {
        lock (gate) Run(() =>
        {
            Drain("size");
            remote.SetLength(value);
            length = remote.Length;
        });
    }
    public void Write(long position, byte[] data)
    {
        lock (gate) Run(() =>
        {
            writes++;
            inputBytes += data.Length;
            var consumed = 0;
            while (consumed < data.Length)
            {
                if (buffer.Length - count <= RecordHeaderBytes || records == MaximumRecords) Drain("full");
                var take = Math.Min(buffer.Length - count - RecordHeaderBytes, data.Length - consumed);
                // Wire format: little-endian file offset, payload length, payload.
                // Records stay in callback order, including overlapping writes.
                BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(count), position + consumed);
                BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(count + sizeof(long)), take);
                data.AsSpan(consumed, take).CopyTo(buffer.AsSpan(count + RecordHeaderBytes));
                count += RecordHeaderBytes + take;
                pendingBytes += take;
                consumed += take;
                records++;
                length = Math.Max(length, position + consumed);
                if (count == buffer.Length) Drain("full");
            }
        });
    }
    public byte[] Read(long position, int size)
    {
        lock (gate)
        {
            byte[] result = [];
            Run(() =>
            {
                Drain("read");
                result = remote.Read(position, size);
            });
            return result;
        }
    }
    public void Flush()
    {
        lock (gate) Run(() =>
        {
            Drain("flush");
            remote.Flush();
        });
    }


    public void Report()
    {
        lock (gate)
        {
            lastReport = elapsed.Elapsed;
            log.Write(FormattableString.Invariant($"HTTPS FFU upload: {writes} incoming writes, {inputBytes} bytes; {uploads} HTTP data writes, {uploadedBytes} bytes uploaded; {pendingBytes} bytes buffered; {elapsed.Elapsed.TotalSeconds:F1}s elapsed, {(double)uploadTicks / Stopwatch.Frequency:F1}s uploading. Batch drains: full={fullDrains}, read={readDrains}, resize={sizeDrains}, flush={flushDrains}."));
        }
    }
}
