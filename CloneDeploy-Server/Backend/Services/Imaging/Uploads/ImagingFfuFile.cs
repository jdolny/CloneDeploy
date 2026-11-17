using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Text.Json;
using CloneDeploy_Server.Backend.Data.Models;
using CloneDeploy_Server.Backend.Enum;
using Microsoft.EntityFrameworkCore;

namespace CloneDeploy_Server.Backend.Services;

public sealed record FfuFileState(long Sequence, string Fingerprint, bool Exists, long Length);

public sealed partial class ImagingUploadService
{
    // The upload row serializes mutations, reads, abort and publication across app instances.
    // The durable last-operation receipt makes identical retries safe after a lost response.
    public async Task<FfuFileState> MutateFfuAsync(Guid id, int userId, long sequence, string operation, long offset, Stream input, CancellationToken ct)
    {
        if (sequence < 1 || offset < 0 || offset > MaximumBytes || operation is not ("create" or "write" or "batch" or "length" or "flush"))
            throw new InvalidDataException("Invalid FFU file operation.");
        using var data = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (data.Length + count > ChunkBytes) throw new InvalidDataException("FFU write is too large.");
            data.Write(buffer, 0, count);
        }
        if (operation is "write" or "batch" ? data.Length == 0 || offset > MaximumBytes - data.Length : data.Length != 0)
            throw new InvalidDataException("Invalid FFU operation content.");
        // Validate every record before changing the file. Ordered offset/length/data
        // records allow DISM's nonsequential writes to share one transaction/receipt.
        var batch = operation == "batch" ? ParseFfuBatch(data.GetBuffer().AsSpan(0, (int)data.Length)) : null;
        if (operation == "batch" && offset != 0) throw new InvalidDataException("Invalid FFU batch offset.");
        var fingerprint = $"{operation}:{offset}:{Convert.ToHexString(SHA256.HashData(data.GetBuffer().AsSpan(0, (int)data.Length)))}";
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var upload = await LockAsync(db, id, userId, ct);
        Pending(upload); await ReadyAsync(db, upload, ct);
        if (upload.Format != ImageFormat.Ffu || !File.Exists(FilePath(upload, "capture.state.json"))) throw new InvalidOperationException("Upload is not an HTTPS FFU capture.");
        var task = await db.ActiveImagingTasks.SingleOrDefaultAsync(x => x.UploadId == id && x.Status < ImagingStatus.Succeeded, ct);
        if (task is null)
            throw new InvalidOperationException("Capture is no longer active.");
        var state = await ReadFfuStateAsync(upload, ct);
        if (sequence == state.Sequence && fingerprint == state.Fingerprint) return state;
        if (sequence != state.Sequence + 1) throw new InvalidOperationException("FFU operation sequence conflict.");
        if (operation == "create" ? state.Exists : !state.Exists) throw new InvalidOperationException("FFU file creation state conflicts.");
        var path = FilePath(upload, "capture.ffu");
        await using (var file = new FileStream(path, operation == "create" ? FileMode.Create : FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 65536, true))
        {
            if (operation == "write") { file.Position = offset; data.Position = 0; await data.CopyToAsync(file, ct); }
            if (batch is not null)
                foreach (var part in batch)
                {
                    file.Position = part.Offset;
                    await file.WriteAsync(data.GetBuffer().AsMemory(part.Start, part.Count), ct);
                }
            if (operation == "length") file.SetLength(offset);
            await file.FlushAsync(ct);
            if (operation == "flush") file.Flush(flushToDisk: true);
            state = new(sequence, fingerprint, true, file.Length);
        }
        await SaveFfuStateAsync(upload, state, ct);
        upload.SizeBytes = state.Length;
        task.BytesTransferred = state.Length; task.LastUpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return state;
    }

    private sealed record FfuReceipt(FfuFileState State, string Checksum);

    private static List<(long Offset, int Start, int Count)> ParseFfuBatch(ReadOnlySpan<byte> data)
    {
        var parts = new List<(long, int, int)>();
        for (var position = 0; position < data.Length;)
        {
            if (data.Length - position < 12 || parts.Count >= 65536) throw new InvalidDataException("Invalid FFU write batch.");
            var offset = BinaryPrimitives.ReadInt64LittleEndian(data[position..]);
            var count = BinaryPrimitives.ReadInt32LittleEndian(data[(position + 8)..]);
            position += 12;
            if (count <= 0 || count > data.Length - position || offset < 0 || offset > MaximumBytes - count)
                throw new InvalidDataException("Invalid FFU batch range.");
            parts.Add((offset, position, count)); position += count;
        }
        return parts;
    }

    private static string ReceiptChecksum(FfuFileState state) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(state)));

    private async Task<FfuFileState> ReadFfuStateAsync(ImageUpload upload, CancellationToken ct)
    {
        FfuFileState? latest = null;
        foreach (var name in new[] { "capture.state.json", "capture.state.tmp" })
        {
            try
            {
                var receipt = JsonSerializer.Deserialize<FfuReceipt>(await File.ReadAllTextAsync(FilePath(upload, name), ct));
                if (receipt?.State is { } state && state.Sequence >= 0 && receipt.Checksum == ReceiptChecksum(state) &&
                    (latest is null || state.Sequence > latest.Sequence)) latest = state;
            }
            catch (FileNotFoundException) { }
            catch (JsonException) { } // An interrupted write leaves the other slot intact.
        }
        return latest ?? throw new InvalidDataException("No valid FFU upload receipt remains. Start a new capture.");
    }

    private async Task SaveFfuStateAsync(ImageUpload upload, FfuFileState state, CancellationToken ct)
    {
        // Alternate slots instead of renaming over a file: some mounted storage providers
        // permit writes but reject replacement. Never overwrite the latest valid receipt.
        // A torn slot is rejected by its checksum and the previous operation can be retried.
        var name = state.Sequence % 2 == 0 ? "capture.state.json" : "capture.state.tmp";
        await using var file = new FileStream(FilePath(upload, name), FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(file, new FfuReceipt(state, ReceiptChecksum(state)), cancellationToken: ct);
        file.Flush(true);
    }

    public async Task<byte[]> ReadFfuUploadAsync(Guid id, int userId, long offset, int count, CancellationToken ct)
    {
        if (offset < 0 || count is < 1 or > ChunkBytes || offset > MaximumBytes - count) throw new InvalidDataException("Invalid FFU read range.");
        await using var db = await factory.CreateContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var upload = await LockAsync(db, id, userId, ct);
        Pending(upload); await ReadyAsync(db, upload, ct);
        if (upload.Format != ImageFormat.Ffu || !File.Exists(FilePath(upload, "capture.state.json"))) throw new InvalidOperationException("Upload is not an HTTPS FFU capture.");
        if (!await db.ActiveImagingTasks.AnyAsync(x => x.UploadId == id && x.Status < ImagingStatus.Succeeded, ct)) throw new InvalidOperationException("Capture is no longer active.");
        await using var file = new FileStream(FilePath(upload, "capture.ffu"), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        if (offset > file.Length - count) throw new InvalidDataException("FFU read exceeds file length.");
        file.Position = offset;
        var bytes = new byte[count]; await file.ReadExactlyAsync(bytes, ct);
        return bytes;
    }
}
