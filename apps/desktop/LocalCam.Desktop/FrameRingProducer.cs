using System.IO.MemoryMappedFiles;
using LocalCam.Contracts;

namespace LocalCam.Desktop;

internal sealed class FrameRingProducer : IDisposable
{
    private const int Width = 1920, Height = 1080, PayloadLength = Width * Height * 3 / 2;
    private readonly MemoryMappedFile mapping;
    private readonly MemoryMappedViewAccessor view;
    private readonly EventWaitHandle frameAvailable;
    private readonly FramePipeServer pipeServer = new(PayloadLength);
    private long sequence;
    public FrameRingProducer()
    {
        FrameIpcContract.ValidateLayout();
        mapping = MemoryMappedFile.CreateOrOpen(FrameIpcContract.MappingName,
            FrameIpcContract.RequiredMappingBytes(PayloadLength), MemoryMappedFileAccess.ReadWrite);
        SharedMemorySecurity.AllowCameraServiceRead(mapping.SafeMemoryMappedFileHandle);
        view = mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
        frameAvailable = new EventWaitHandle(false, EventResetMode.AutoReset, FrameIpcContract.FrameAvailableEventName);
        view.Write(0, FrameIpcContract.Magic); view.Write(4, FrameIpcContract.MajorVersion);
        view.Write(6, FrameIpcContract.MinorVersion); view.Write(8, FrameIpcContract.SlotCount);
        view.Write(12, (uint)PayloadLength); view.Write(16, 0L);
        view.Write(24, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }
    public void Publish(byte[] nv12)
    {
        var timestamp = DateTime.UtcNow.ToFileTimeUtc();
        pipeServer.Publish(nv12, timestamp);
        var seq = ++sequence;
        var offset = 64 + (int)((seq - 1) % FrameIpcContract.SlotCount) * (64 + PayloadLength);
        view.Write(offset, 0L);
        Thread.MemoryBarrier();
        view.Write(offset + 8, timestamp); view.Write(offset + 16, (uint)Width);
        view.Write(offset + 20, (uint)Height); view.Write(offset + 24, (uint)Width);
        view.Write(offset + 28, (uint)FramePixelFormat.Nv12); view.Write(offset + 32, (uint)PayloadLength);
        view.Write(offset + 36, 0u);
        view.WriteArray(offset + 64, nv12, 0, PayloadLength);
        Thread.MemoryBarrier();
        view.Write(offset, seq); view.Write(16, seq); frameAvailable.Set();
    }
    public void Clear() { pipeServer.Clear(); view.Write(16, 0L); frameAvailable.Set(); }
    public void Dispose() { pipeServer.Dispose(); frameAvailable.Dispose(); view.Dispose(); mapping.Dispose(); }
}
