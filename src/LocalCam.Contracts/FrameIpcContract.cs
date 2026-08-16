using System.Runtime.InteropServices;

namespace LocalCam.Contracts;

/// <summary>
/// Binary contract shared by LocalCam.Desktop and the future native Media Foundation source.
/// All multibyte values are little-endian. A producer publishes payload bytes first and writes
/// Sequence last; readers accept a slot only when its sequence is stable and newer than theirs.
/// </summary>
public static class FrameIpcContract
{
    public const uint Magic = 0x4D41434C; // "LCAM" in little-endian memory.
    public const ushort MajorVersion = 1;
    public const ushort MinorVersion = 0;
    public const uint SlotCount = 4;
    public const string MappingName = "Local\\LocalCam.FrameRing.v1";
    public const string FrameAvailableEventName = "Local\\LocalCam.FrameAvailable.v1";
    public const string FramePipeName = "LocalCam.FramePipe.v1";

    public static int RequiredMappingBytes(uint slotPayloadCapacity) => checked(
        Marshal.SizeOf<FrameRingHeader>() +
        (int)SlotCount * checked(Marshal.SizeOf<FrameSlotHeader>() + (int)slotPayloadCapacity));

    public static void ValidateLayout()
    {
        if (Marshal.SizeOf<FrameRingHeader>() != 64 || Marshal.SizeOf<FrameSlotHeader>() != 64)
        {
            throw new InvalidOperationException("Frame IPC headers must remain 64 bytes for the native source ABI.");
        }
    }
}

public enum FramePixelFormat : uint
{
    Nv12 = 0x3231564E // 'NV12'
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct FrameRingHeader
{
    public uint Magic;
    public ushort MajorVersion;
    public ushort MinorVersion;
    public uint SlotCount;
    public uint SlotPayloadCapacity;
    public long LatestSequence;
    public long ProducerEpoch;
    public long Reserved0;
    public long Reserved1;
    public long Reserved2;
    public long Reserved3;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct FrameSlotHeader
{
    // A sequence of zero means the slot has never been published.
    public long Sequence;
    public long Timestamp100Nanoseconds;
    public uint Width;
    public uint Height;
    public uint Stride;
    public FramePixelFormat PixelFormat;
    public uint PayloadLength;
    public uint Flags;
    public long Reserved0;
    public long Reserved1;
    public long Reserved2;
}
