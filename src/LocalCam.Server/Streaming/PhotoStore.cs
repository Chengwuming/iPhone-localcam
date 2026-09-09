using System.Buffers.Binary;
namespace LocalCam.Server.Streaming;

public sealed record CapturedPhoto(long Sequence, byte[] Jpeg, int Width, int Height, string Source, DateTimeOffset TakenAt);
public sealed class PhotoStore
{
    public const int MaximumBytes = 24 * 1024 * 1024;
    private readonly object gate = new();
    private long sequence;
    private CapturedPhoto? latest;
    private DateTimeOffset requested;
    private string? requestId;
    public CapturedPhoto? Latest { get { lock (gate) return latest; } }
    public string Request()
    {
        lock (gate) { requested = DateTimeOffset.UtcNow; return requestId ??= Guid.NewGuid().ToString("N"); }
    }
    public string? TakeRequest()
    {
        lock (gate)
        {
            var result = DateTimeOffset.UtcNow - requested < TimeSpan.FromSeconds(30) ? requestId : null;
            requestId = null; return result;
        }
    }
    public CapturedPhoto Accept(byte[] jpeg, string source)
    {
        var (width, height) = Dimensions(jpeg);
        lock (gate) return latest = new(++sequence, jpeg, width, height, source, DateTimeOffset.Now);
    }
    public static (int Width, int Height) Dimensions(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg.Length > MaximumBytes || jpeg[0] != 0xff || jpeg[1] != 0xd8)
            throw new InvalidDataException("需要 JPEG 照片，最大 24 MB。");
        int offset = 2;
        while (offset < jpeg.Length)
        {
            if (jpeg[offset++] != 0xff) break;
            while (offset < jpeg.Length && jpeg[offset] == 0xff) offset++;
            if (offset >= jpeg.Length) break;
            int marker = jpeg[offset++];
            if (marker is 0xda or 0xd9) break;
            if (marker == 0x01 || marker is >= 0xd0 and <= 0xd7) continue;
            if (offset + 2 > jpeg.Length) break;
            int length = BinaryPrimitives.ReadUInt16BigEndian(jpeg[offset..]);
            if (length < 2 || offset + length > jpeg.Length) break;
            if (marker is 0xc0 or 0xc1 or 0xc2)
            {
                if (length < 8) break;
                int h = BinaryPrimitives.ReadUInt16BigEndian(jpeg[(offset + 3)..]);
                int w = BinaryPrimitives.ReadUInt16BigEndian(jpeg[(offset + 5)..]);
                if (w < 16 || h < 16 || w > 8192 || h > 8192 || (long)w * h > 50_000_000) break;
                return (w, h);
            }
            offset += length;
        }
        throw new InvalidDataException("照片损坏或尺寸不受支持。");
    }
}
