using System.Text.Json;
namespace LocalCam.Server.Streaming;

// One phone, one outstanding command. No command is replayed after delivery or disconnect.
public sealed record CameraCommand(long Id, string Kind, double? Value = null, string? Text = null);
public sealed record CameraSnapshot(JsonElement? State, DateTimeOffset UpdatedAt, string Result, bool Pending);
public sealed class CameraControlStore
{
    private readonly object gate = new();
    private JsonElement? state;
    private DateTimeOffset updated, sentAt;
    private CameraCommand? pending;
    private long sequence, outstanding;
    private string result = "等待手机同步相机设置";
    private void Expire()
    {
        if (outstanding != 0 && DateTimeOffset.UtcNow - sentAt > TimeSpan.FromSeconds(75))
        { pending = null; outstanding = 0; result = "手机未完成操作；请检查手机页面后重试"; }
    }
    public CameraSnapshot Snapshot { get { lock (gate) { Expire(); return new(state, updated, result, outstanding != 0); } } }
    public CameraCommand Request(string kind, double? value = null, string? text = null)
    {
        if (kind is not ("quality" or "zoom" or "focus" or "distance" or "refocus" or "photo")) throw new ArgumentException("未知相机操作");
        if (value is { } v && !double.IsFinite(v)) throw new ArgumentException("无效参数");
        lock (gate)
        {
            Expire();
            if (DateTimeOffset.UtcNow - updated > TimeSpan.FromSeconds(5)) throw new InvalidOperationException("请刷新并打开手机 DeskCam");
            if (outstanding != 0) throw new InvalidOperationException("上一项操作尚未完成");
            pending = new(++sequence, kind, value, text); outstanding = sequence; sentAt = DateTimeOffset.UtcNow;
            result = kind == "photo" ? "正在请求高清抓拍…" : "正在应用相机设置…";
            return pending;
        }
    }
    public CameraCommand? Exchange(JsonElement current, long ack, string? message)
    {
        lock (gate)
        {
            Expire(); state = current.Clone(); updated = DateTimeOffset.UtcNow;
            if (ack != 0 && ack == outstanding) { outstanding = 0; result = message ?? "操作完成"; }
            var command = pending; pending = null; return command;
        }
    }
}
