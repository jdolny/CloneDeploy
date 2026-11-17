using System.Buffers.Binary;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>Bounds uploaded PNG artwork before it is stored or sent to portal clients.</summary>
public static class PortalIcon
{
    public const int MaximumBytes = 64 * 1024;
    public static bool IsValid(byte[] data)
    {
        if (data.Length is < 45 or > MaximumBytes || !data.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return false;
        if (BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8)) != 13 || !data.AsSpan(12, 4).SequenceEqual("IHDR"u8)) return false;
        var width = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16));
        var height = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(20));
        if (width is < 1 or > 512 || height is < 1 or > 512) return false;
        var hasData = false;
        for (var offset = 8; offset <= data.Length - 12;)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
            if (length > data.Length - offset - 12) return false;
            var type = data.AsSpan(offset + 4, 4);
            if (type.SequenceEqual("acTL"u8)) return false; // Static icons only.
            if (type.SequenceEqual("IDAT"u8)) hasData = true;
            offset += (int)length + 12;
            if (type.SequenceEqual("IEND"u8)) return hasData && length == 0 && offset == data.Length;
        }
        return false;
    }
}
