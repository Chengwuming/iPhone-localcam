using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace LocalCam.Server.Streaming;

public sealed class FrameRelay
{
    private const int MaximumFrameSize = 2 * 1024 * 1024;
    private readonly ConcurrentDictionary<Guid, WebSocket> monitors = new();
    private readonly object phoneLock = new();
    private WebSocket? activePhone;
    private long framesReceived;
    private long bytesReceived;
    private DateTimeOffset? lastFrameAt;

    public long FramesReceived => Interlocked.Read(ref framesReceived);
    public long BytesReceived => Interlocked.Read(ref bytesReceived);
    public DateTimeOffset? LastFrameAt => lastFrameAt;
    public int MonitorCount => monitors.Count;
    public bool IsPhoneConnected
    {
        get
        {
            lock (phoneLock)
            {
                return activePhone?.State == WebSocketState.Open;
            }
        }
    }

    public void SetActivePhone(WebSocket phone)
    {
        lock (phoneLock)
        {
            activePhone = phone;
        }
    }

    public void RemoveActivePhone(WebSocket phone)
    {
        lock (phoneLock)
        {
            if (ReferenceEquals(activePhone, phone))
            {
                activePhone = null;
            }
        }
    }

    private string pocMimeType = "uninitialized";
    private long pocConfiguredBps;
    private int pocTrackWidth;
    private int pocTrackHeight;
    private double pocTrackFps;
    private DateTimeOffset? pocSessionStart;
    private DateTimeOffset? pocLastWindow;
    private DateTimeOffset? pocLastChunkAt;
    private long pocTotalBytes;
    private long pocTotalChunks;
    private long pocWindowBytes;
    private long pocWindowChunks;

    public async Task ReceivePhoneFramesAsync(WebSocket phone, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        while (phone.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            await using var data = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await phone.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await phone.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Phone disconnected", CancellationToken.None);
                    return;
                }

                if (data.Length + result.Count > MaximumFrameSize)
                {
                    await phone.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Frame exceeds 2 MiB", CancellationToken.None);
                    return;
                }

                await data.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);
            }
            while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Text)
            {
                var text = System.Text.Encoding.UTF8.GetString(data.ToArray());
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("type", out var typeEl) && typeEl.GetString() == "poc.meta")
                    {
                        pocMimeType = root.TryGetProperty("mimeType", out var m) ? m.GetString() ?? "unknown" : "unknown";
                        pocConfiguredBps = root.TryGetProperty("vBitrate", out var b) ? b.GetInt64() : 0;
                        pocTrackWidth = root.TryGetProperty("trackWidth", out var w) ? w.GetInt32() : 0;
                        pocTrackHeight = root.TryGetProperty("trackHeight", out var h) ? h.GetInt32() : 0;
                        pocTrackFps = root.TryGetProperty("trackFps", out var f) ? f.GetDouble() : 0;

                        pocSessionStart = DateTimeOffset.UtcNow;
                        pocLastWindow = DateTimeOffset.UtcNow;
                        pocWindowBytes = 0;
                        pocWindowChunks = 0;
                        pocTotalBytes = 0;
                        pocTotalChunks = 0;
                        pocLastChunkAt = null;

                        Console.WriteLine();
                        Console.WriteLine("================================================================");
                        Console.WriteLine($"[MediaRecorder PoC] 手机连接成功!");
                        Console.WriteLine($"  实际 Codec: {pocMimeType}");
                        Console.WriteLine($"  配置 Bitrate: {(pocConfiguredBps > 0 ? (pocConfiguredBps / 1_000_000.0).ToString("F2") + " Mbps" : "默认(Safari自适应)")}");
                        Console.WriteLine($"  相机源设置: {pocTrackWidth}x{pocTrackHeight} @ {pocTrackFps:F0}fps");
                        Console.WriteLine("================================================================");
                    }
                }
                catch
                {
                }
                continue;
            }

            if (result.MessageType != WebSocketMessageType.Binary || data.Length == 0)
            {
                continue;
            }

            var chunkLength = data.Length;
            var now = DateTimeOffset.UtcNow;
            if (pocSessionStart is null)
            {
                pocSessionStart = now;
                pocLastWindow = now;
            }

            pocTotalBytes += chunkLength;
            pocTotalChunks++;
            pocWindowBytes += chunkLength;
            pocWindowChunks++;

            var chunkIntervalMs = pocLastChunkAt.HasValue ? (now - pocLastChunkAt.Value).TotalMilliseconds : 0;
            pocLastChunkAt = now;

            var dt = (now - (pocLastWindow ?? now)).TotalSeconds;
            if (dt >= 1.0)
            {
                var observedMbps = (pocWindowBytes * 8.0) / (dt * 1_000_000.0);
                var avgChunkKb = (pocWindowBytes / 1024.0) / Math.Max(1, pocWindowChunks);
                var avgIntervalMs = (dt * 1000.0) / Math.Max(1, pocWindowChunks);
                var elapsed = now - pocSessionStart.Value;
                var stableMark = elapsed.TotalSeconds >= 120 ? "[2分钟稳定达标 ✓]" : "[测试中]";

                Console.WriteLine($"[{elapsed:mm\\:ss}] {stableMark} Codec: {pocMimeType} | 实际码率: {observedMbps,5:F2} Mbps | 块间隔: {avgIntervalMs,5:F1} ms (最新:{chunkIntervalMs,4:F0}ms) | 块大小: {avgChunkKb,4:F1} KB | 总块数: {pocTotalChunks,5} | 总流量: {pocTotalBytes / (1024.0 * 1024.0),5:F2} MB");

                pocWindowBytes = 0;
                pocWindowChunks = 0;
                pocLastWindow = now;
            }

            Interlocked.Increment(ref framesReceived);
            Interlocked.Add(ref bytesReceived, chunkLength);
            lastFrameAt = now;
        }
    }

    public async Task AddMonitorAsync(WebSocket monitor, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        monitors[id] = monitor;
        var buffer = new byte[4096];
        try
        {
            while (monitor.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await monitor.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await monitor.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Monitor closed", CancellationToken.None);
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text && result.EndOfMessage)
                {
                    await ForwardControlAsync(buffer.AsMemory(0, result.Count), cancellationToken);
                }
            }
        }
        finally
        {
            monitors.TryRemove(id, out _);
        }
    }

    private async Task BroadcastFrameAsync(byte[] frame, CancellationToken cancellationToken)
    {
        foreach (var monitor in monitors.ToArray())
        {
            if (monitor.Value.State != WebSocketState.Open)
            {
                monitors.TryRemove(monitor.Key, out _);
                continue;
            }

            try
            {
                await monitor.Value.SendAsync(frame, WebSocketMessageType.Binary, true, cancellationToken);
            }
            catch (WebSocketException)
            {
                monitors.TryRemove(monitor.Key, out _);
            }
        }
    }

    private async Task ForwardControlAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        if (!IsSupportedControl(message.Span))
        {
            return;
        }

        WebSocket? phone;
        lock (phoneLock)
        {
            phone = activePhone;
        }

        if (phone?.State == WebSocketState.Open)
        {
            await phone.SendAsync(message, WebSocketMessageType.Text, true, cancellationToken);
        }
    }

    private static bool IsSupportedControl(ReadOnlySpan<byte> message)
    {
        try
        {
            using var document = JsonDocument.Parse(message.ToArray());
            if (!document.RootElement.TryGetProperty("type", out var type))
            {
                return false;
            }

            if (type.ValueEquals("camera.switch") && document.RootElement.TryGetProperty("facing", out var facing))
            {
                return facing.ValueEquals("user") || facing.ValueEquals("environment");
            }

            if (type.ValueEquals("camera.zoom") && document.RootElement.TryGetProperty("zoom", out var zoom))
            {
                return zoom.TryGetDouble(out var zoomValue) && zoomValue is >= 0.5 and <= 10;
            }

            return type.ValueEquals("capture.fps") &&
                   document.RootElement.TryGetProperty("fps", out var fps) &&
                   fps.TryGetInt32(out var value) && value is 15 or 20 or 30;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
