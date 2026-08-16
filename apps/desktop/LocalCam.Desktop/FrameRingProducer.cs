using System.IO.MemoryMappedFiles;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LocalCam.Contracts;

namespace LocalCam.Desktop;

internal sealed class FrameRingProducer : IDisposable
{
    private const int Width = 640;
    private const int Height = 480;
    private const int Stride = Width;
    private const int PayloadLength = Width * Height * 3 / 2;
    private const int RingHeaderBytes = 64;
    private const int SlotHeaderBytes = 64;

    private readonly MemoryMappedFile mapping;
    private readonly MemoryMappedViewAccessor view;
    private readonly EventWaitHandle frameAvailable;
    private readonly FramePipeServer pipeServer = new(PayloadLength);
    private readonly byte[] bgra = new byte[Width * Height * 4];
    private readonly byte[] nv12 = new byte[PayloadLength];
    private readonly RenderTargetBitmap renderTarget = new(Width, Height, 96, 96, PixelFormats.Pbgra32);
    private long sequence;

    public FrameRingProducer()
    {
        FrameIpcContract.ValidateLayout();
        mapping = MemoryMappedFile.CreateOrOpen(
            FrameIpcContract.MappingName,
            FrameIpcContract.RequiredMappingBytes(PayloadLength),
            MemoryMappedFileAccess.ReadWrite);
        SharedMemorySecurity.AllowCameraServiceRead(mapping.SafeMemoryMappedFileHandle);
        view = mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
        frameAvailable = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            FrameIpcContract.FrameAvailableEventName);

        view.Write(0, FrameIpcContract.Magic);
        view.Write(4, FrameIpcContract.MajorVersion);
        view.Write(6, FrameIpcContract.MinorVersion);
        view.Write(8, FrameIpcContract.SlotCount);
        view.Write(12, (uint)PayloadLength);
        view.Write(16, 0L);
        view.Write(24, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public void Publish(BitmapSource source, bool mirrored)
    {
        RenderToBgra(source, mirrored);
        ConvertBgraToNv12();
        var timestamp = DateTime.UtcNow.ToFileTimeUtc();
        pipeServer.Publish(nv12, timestamp);

        var nextSequence = Interlocked.Increment(ref sequence);
        var slotIndex = (int)((nextSequence - 1) % FrameIpcContract.SlotCount);
        var slotOffset = RingHeaderBytes + slotIndex * (SlotHeaderBytes + PayloadLength);

        view.Write(slotOffset, 0L);
        view.Write(slotOffset + 8, timestamp);
        view.Write(slotOffset + 16, (uint)Width);
        view.Write(slotOffset + 20, (uint)Height);
        view.Write(slotOffset + 24, (uint)Stride);
        view.Write(slotOffset + 28, (uint)FramePixelFormat.Nv12);
        view.Write(slotOffset + 32, (uint)PayloadLength);
        view.Write(slotOffset + 36, 0u);
        view.WriteArray(slotOffset + SlotHeaderBytes, nv12, 0, nv12.Length);
        Thread.MemoryBarrier();
        view.Write(slotOffset, nextSequence);
        view.Write(16, nextSequence);
        frameAvailable.Set();
    }

    public void Clear()
    {
        pipeServer.Clear();
        view.Write(16, 0L);
        frameAvailable.Set();
    }

    private void RenderToBgra(BitmapSource source, bool mirrored)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(System.Windows.Media.Brushes.Black, null, new Rect(0, 0, Width, Height));
            var scale = Math.Min((double)Width / source.PixelWidth, (double)Height / source.PixelHeight);
            var renderWidth = source.PixelWidth * scale;
            var renderHeight = source.PixelHeight * scale;
            if (mirrored)
            {
                drawing.PushTransform(new ScaleTransform(-1, 1, Width / 2d, Height / 2d));
            }
            drawing.DrawImage(
                source,
                new Rect(
                    (Width - renderWidth) / 2,
                    (Height - renderHeight) / 2,
                    renderWidth,
                    renderHeight));
            if (mirrored)
            {
                drawing.Pop();
            }
        }
        renderTarget.Render(visual);
        renderTarget.CopyPixels(bgra, Width * 4, 0);
    }

    private void ConvertBgraToNv12()
    {
        for (var y = 0; y < Height; y++)
        {
            var sourceRow = y * Width * 4;
            var targetRow = y * Width;
            for (var x = 0; x < Width; x++)
            {
                var pixel = sourceRow + x * 4;
                var blue = bgra[pixel];
                var green = bgra[pixel + 1];
                var red = bgra[pixel + 2];
                nv12[targetRow + x] = Clamp(((66 * red + 129 * green + 25 * blue + 128) >> 8) + 16);
            }
        }

        var uvBase = Width * Height;
        for (var y = 0; y < Height; y += 2)
        {
            for (var x = 0; x < Width; x += 2)
            {
                var red = 0;
                var green = 0;
                var blue = 0;
                for (var row = 0; row < 2; row++)
                {
                    for (var column = 0; column < 2; column++)
                    {
                        var pixel = ((y + row) * Width + x + column) * 4;
                        blue += bgra[pixel];
                        green += bgra[pixel + 1];
                        red += bgra[pixel + 2];
                    }
                }

                red /= 4;
                green /= 4;
                blue /= 4;
                var uv = uvBase + (y / 2) * Width + x;
                nv12[uv] = Clamp(((-38 * red - 74 * green + 112 * blue + 128) >> 8) + 128);
                nv12[uv + 1] = Clamp(((112 * red - 94 * green - 18 * blue + 128) >> 8) + 128);
            }
        }
    }

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);

    public void Dispose()
    {
        pipeServer.Dispose();
        frameAvailable.Dispose();
        view.Dispose();
        mapping.Dispose();
    }
}
