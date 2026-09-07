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

            if (result.MessageType != WebSocketMessageType.Binary || data.Length == 0)
            {
                continue;
            }

            var frame = data.ToArray();
            Interlocked.Increment(ref framesReceived);
            Interlocked.Add(ref bytesReceived, frame.Length);
            lastFrameAt = DateTimeOffset.UtcNow;
            await BroadcastFrameAsync(frame, cancellationToken);
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
