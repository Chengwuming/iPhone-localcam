using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
namespace LocalCam.Server.Streaming;
public sealed class FrameRelay
{
    private readonly Channel<VideoPacket> packets = Channel.CreateBounded<VideoPacket>(new BoundedChannelOptions(3)
    { FullMode = BoundedChannelFullMode.Wait, SingleReader = false, SingleWriter = false });
    private WebSocket? activePhone;
    private long connectionId, framesReceived, bytesReceived, lastFrameTicks;
    private int requestKey;
    public ChannelReader<VideoPacket> Packets => packets.Reader;
    public bool IsPhoneConnected => activePhone?.State == WebSocketState.Open;
    public long ConnectionId => Interlocked.Read(ref connectionId);
    public long FramesReceived => Interlocked.Read(ref framesReceived);
    public long BytesReceived => Interlocked.Read(ref bytesReceived);
    public DateTimeOffset? LastFrameAt => Interlocked.Read(ref lastFrameTicks) is > 0 and var ticks ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;
    public string? Error { get; private set; }
    public void RequestKeyFrame() => Interlocked.Exchange(ref requestKey, 1);
    public async Task ReceivePhoneFramesAsync(WebSocket phone, CancellationToken cancellationToken)
    {
        var previous = Interlocked.Exchange(ref activePhone, phone);
        previous?.Abort();
        var id = Interlocked.Increment(ref connectionId);
        while (packets.Reader.TryRead(out _)) { }
        Error = null;
        bool waitingForKey = true;
        uint? stream = null, sequence = null;
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (phone.State == WebSocketState.Open && id == ConnectionId)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await phone.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    if (message.Length + result.Count > VideoProtocol.MaximumMessageSize) throw new InvalidDataException("视频包过大");
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                if (id != ConnectionId) return;
                if (result.MessageType != WebSocketMessageType.Binary) continue;
                var packet = VideoProtocol.Parse(message.GetBuffer().AsSpan(0, (int)message.Length), id);
                if (stream != packet.StreamId || (sequence.HasValue && packet.Sequence != unchecked(sequence.Value + 1)))
                    waitingForKey = true;
                stream = packet.StreamId;
                sequence = packet.Sequence;
                if (Interlocked.Exchange(ref requestKey, 0) != 0) waitingForKey = true;
                if (waitingForKey && !packet.KeyFrame)
                {
                    await SendKeyRequest(phone, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (!packets.Writer.TryWrite(packet))
                {
                    while (packets.Reader.TryRead(out _)) { }
                    waitingForKey = true;
                    if (packet.KeyFrame) { packets.Writer.TryWrite(packet); waitingForKey = false; }
                    else await SendKeyRequest(phone, cancellationToken).ConfigureAwait(false);
                }
                else waitingForKey = false;
                Interlocked.Increment(ref framesReceived);
                Interlocked.Add(ref bytesReceived, packet.Data.Length);
                Interlocked.Exchange(ref lastFrameTicks, DateTimeOffset.UtcNow.Ticks);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (ex is WebSocketException or InvalidDataException)
        { if (id == ConnectionId) Error = ex.Message; }
        finally
        {
            Interlocked.CompareExchange(ref activePhone, null, phone);
            phone.Abort();
        }
    }
    private static Task SendKeyRequest(WebSocket phone, CancellationToken ct) =>
        phone.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("{\"type\":\"keyframe\"}")), WebSocketMessageType.Text, true, ct);
}
