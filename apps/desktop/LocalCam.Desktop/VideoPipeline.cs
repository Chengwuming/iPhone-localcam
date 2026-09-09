using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LocalCam.Server.Streaming;

namespace LocalCam.Desktop;

internal sealed record ViewTransform(int Rotation = 0, double X = 0, double Y = 0, double Width = 1, double Height = 1)
{
    public ViewTransform Normalize() => new(((Rotation % 4) + 4) % 4,
        Math.Clamp(double.IsFinite(X) ? X : 0, 0, 1 - Math.Clamp(double.IsFinite(Width) ? Width : 1, .05, 1)),
        Math.Clamp(double.IsFinite(Y) ? Y : 0, 0, 1 - Math.Clamp(double.IsFinite(Height) ? Height : 1, .05, 1)),
        Math.Clamp(double.IsFinite(Width) ? Width : 1, .05, 1), Math.Clamp(double.IsFinite(Height) ? Height : 1, .05, 1));
}

internal sealed class DisplayFrame : IDisposable
{
    private int references = 1;
    public const int Width = 1920, Height = 1080, BgraLength = Width * Height * 4, Nv12Length = Width * Height * 3 / 2;
    public byte[] Bgra { get; } = ArrayPool<byte>.Shared.Rent(BgraLength);
    public byte[] Nv12 { get; } = ArrayPool<byte>.Shared.Rent(Nv12Length);
    public int[] Content { get; } = new int[4];
    public long Sequence { get; set; }
    public long CapturedTimestampUs { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public long ConnectionId { get; set; }
    public void AddRef() => Interlocked.Increment(ref references);
    public void Dispose()
    {
        if (Interlocked.Decrement(ref references) == 0)
        { ArrayPool<byte>.Shared.Return(Bgra); ArrayPool<byte>.Shared.Return(Nv12); }
    }
    public BitmapSource ToBitmap(bool crop)
    {
        var bitmap = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, Bgra, Width * 4);
        bitmap.Freeze();
        if (!crop) return bitmap;
        var result = new CroppedBitmap(bitmap, new Int32Rect(Content[0], Content[1], Content[2], Content[3]));
        result.Freeze(); return result;
    }
}
internal static class NativeVideo
{
    [DllImport("DeskCamVideo", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int dc_create(int width, int height, out IntPtr handle, out int hardware);
    [DllImport("DeskCamVideo", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int dc_decode(IntPtr handle, byte[] data, int length, long timestampUs, int key,
        byte[] output, int capacity, out int produced);
    [DllImport("DeskCamVideo", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void dc_destroy(IntPtr handle);
    [DllImport("DeskCamVideo", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int dc_render(byte[] input, int width, int height, int rotation,
        double x, double y, double cropWidth, double cropHeight, byte[] output, int outWidth, int outHeight,
        byte[] bgra, int[] content);
    internal static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
}
internal sealed class VideoPipeline : IDisposable
{
    private readonly FrameRelay relay;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object gate = new();
    private readonly Task worker;
    private DisplayFrame? latest;
    private ViewTransform transform;
    private long sequence;
    private long lastDecoded;
    private long decodedFrames;
    private double fps, processingMs;
    private string decoderName = "等待视频";
    private string? error;
    public VideoPipeline(FrameRelay relay, ViewTransform transform)
    {
        this.relay = relay; this.transform = transform.Normalize();
        // A dedicated MTA thread owns COM and native decoder lifetime, with no async thread switches.
        worker = Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
    public ViewTransform Transform { get => Volatile.Read(ref transform); set => Volatile.Write(ref transform, value.Normalize()); }
    public DisplayFrame? Acquire()
    {
        lock (gate)
        {
            if (latest is null || !relay.IsPhoneConnected || latest.ConnectionId != relay.ConnectionId ||
                Stopwatch.GetElapsedTime(Interlocked.Read(ref lastDecoded)).TotalSeconds > 2) return null;
            latest.AddRef(); return latest;
        }
    }
    public object Status => new
    {
        fps = Volatile.Read(ref fps), processedFrames = Interlocked.Read(ref decodedFrames),
        processingMs = Volatile.Read(ref processingMs), decoder = decoderName, error,
        outputWidth = DisplayFrame.Width, outputHeight = DisplayFrame.Height,
        frameAgeMs = lastDecoded == 0 ? (double?)null : Stopwatch.GetElapsedTime(Interlocked.Read(ref lastDecoded)).TotalMilliseconds
    };
    public string Description => error is not null ? "视频错误：" + error :
        !relay.IsPhoneConnected ? "等待手机 · 滚轮缩放，拖动平移 · Ctrl+Alt+C 全局复制" :
        $"{decoderName} · {fps:F1} fps · 处理 {processingMs:F0} ms · 滚轮缩放 / 拖动平移";
    public byte[]? Snapshot()
    {
        using var frame = Acquire();
        if (frame is null) return null;
        var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
        encoder.Frames.Add(BitmapFrame.Create(frame.ToBitmap(true)));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
    private void Run()
    {
        IntPtr decoder = IntPtr.Zero;
        FrameRingProducer? publisher = null;
        byte[]? raw = null;
        VideoPacket? previous = null;
        long window = Stopwatch.GetTimestamp(), count = 0;
        bool published = false;
        try
        {
            publisher = new FrameRingProducer();
            while (!lifetime.IsCancellationRequested)
            {
                if (!relay.Packets.TryRead(out var packet))
                {
                    if (published && (!relay.IsPhoneConnected || Stopwatch.GetElapsedTime(lastDecoded).TotalSeconds > 2))
                    { publisher.Clear(); published = false; }
                    lifetime.Token.WaitHandle.WaitOne(5);
                    continue;
                }
                if (!relay.IsPhoneConnected || packet.ConnectionId != relay.ConnectionId) continue;
                if (Stopwatch.GetElapsedTime(packet.ReceivedAt).TotalMilliseconds > 250)
                { relay.RequestKeyFrame(); previous = null; continue; }
                var reset = previous is null || previous.ConnectionId != packet.ConnectionId ||
                    previous.StreamId != packet.StreamId || previous.Width != packet.Width || previous.Height != packet.Height ||
                    packet.Sequence != unchecked(previous.Sequence + 1);
                if (reset && !packet.KeyFrame) { previous = null; relay.RequestKeyFrame(); continue; }
                try
                {
                    if (reset)
                    {
                        if (decoder != IntPtr.Zero) { NativeVideo.dc_destroy(decoder); decoder = IntPtr.Zero; }
                        NativeVideo.Check(NativeVideo.dc_create(packet.Width, packet.Height, out decoder, out var hardware));
                        decoderName = hardware != 0 ? "H.264 · Media Foundation / DXVA" : "H.264 · Media Foundation / CPU";
                        raw = new byte[packet.Width * packet.Height * 3 / 2];
                    }
                    previous = packet;
                    var started = Stopwatch.GetTimestamp();
                    NativeVideo.Check(NativeVideo.dc_decode(decoder, packet.Data, packet.Data.Length, packet.TimestampUs,
                        packet.KeyFrame ? 1 : 0, raw!, raw!.Length, out var produced));
                    if (produced == 0) continue;
                    var settings = Transform;
                    DisplayFrame? next = new();
                    try
                    {
                        NativeVideo.Check(NativeVideo.dc_render(raw, packet.Width, packet.Height, settings.Rotation,
                            settings.X, settings.Y, settings.Width, settings.Height, next.Nv12, DisplayFrame.Width,
                            DisplayFrame.Height, next.Bgra, next.Content));
                        next.Sequence = ++sequence; next.CapturedTimestampUs = packet.TimestampUs;
                        next.ReceivedAt = DateTimeOffset.UtcNow; next.ConnectionId = packet.ConnectionId;
                        // Check session ownership again after potentially expensive native work.
                        if (packet.ConnectionId != relay.ConnectionId || !relay.IsPhoneConnected) continue;
                        publisher.Publish(next.Nv12); published = true;
                        Interlocked.Exchange(ref lastDecoded, Stopwatch.GetTimestamp());
                        lock (gate) { var old = latest; latest = next; next = null; old?.Dispose(); }
                    }
                    finally { next?.Dispose(); }
                    error = null;
                    processingMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    Interlocked.Increment(ref decodedFrames); count++;
                    var seconds = Stopwatch.GetElapsedTime(window).TotalSeconds;
                    if (seconds >= 1) { fps = count / seconds; count = 0; window = Stopwatch.GetTimestamp(); }
                }
                catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
                {
                    error = ex.Message; previous = null; relay.RequestKeyFrame();
                    if (decoder != IntPtr.Zero) { NativeVideo.dc_destroy(decoder); decoder = IntPtr.Zero; }
                    lifetime.Token.WaitHandle.WaitOne(100);
                }
            }
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            if (decoder != IntPtr.Zero) NativeVideo.dc_destroy(decoder);
            publisher?.Clear(); publisher?.Dispose();
        }
    }
    public void Dispose()
    {
        lifetime.Cancel(); worker.GetAwaiter().GetResult();
        lock (gate) { latest?.Dispose(); latest = null; }
        lifetime.Dispose();
    }
}
