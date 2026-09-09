using System.Buffers.Binary;
namespace LocalCam.Server.Streaming;
public sealed record VideoPacket(int Width, int Height, uint StreamId, uint Sequence, long TimestampUs,
    bool KeyFrame, byte[] Data, long ConnectionId, long ReceivedAt, uint ColorFlags = 0);
public static class VideoProtocol
{
    public const int HeaderSize = 32;
    public const int MaximumMessageSize = 2 * 1024 * 1024;
    public static VideoPacket Parse(ReadOnlySpan<byte> bytes, long connectionId)
    {
        if (bytes.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0x31564344)
            throw new InvalidDataException("Invalid DeskCam video header.");
        int width = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[28..]);
        if (width < 16 || height < 16 || width > 1920 || height > 1920 ||
            width * height > 1920 * 1080 || (width & 1) != 0 || (height & 1) != 0 ||
            length == 0 || length > MaximumMessageSize - HeaderSize || length != bytes.Length - HeaderSize)
            throw new InvalidDataException("Invalid video dimensions or payload size.");
        long timestamp = BinaryPrimitives.ReadInt64LittleEndian(bytes[16..]);
        if (timestamp < 0 || timestamp > long.MaxValue / 10) throw new InvalidDataException("Invalid timestamp.");
        return new(width, height, BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]), timestamp,
            (BinaryPrimitives.ReadUInt32LittleEndian(bytes[24..]) & 1) != 0,
            bytes[HeaderSize..].ToArray(), connectionId, System.Diagnostics.Stopwatch.GetTimestamp(),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[24..]) & 6);
    }
}
