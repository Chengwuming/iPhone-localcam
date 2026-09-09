using System.Buffers.Binary;
using LocalCam.Server.Pairing;
using LocalCam.Server.Streaming;

internal static class DeskCamTests
{
    public static void Run()
    {
        var packet = new byte[36];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, 0x31564344);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4), 1920);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6), 1080);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), 7);
        BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(16), 123456);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(24), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(28), 4);
        packet[35] = 0x67;
        var parsed = VideoProtocol.Parse(packet, 9);
        Assert(parsed.Width == 1920 && parsed.Height == 1080 && parsed.KeyFrame &&
            parsed.TimestampUs == 123456 && parsed.ConnectionId == 9 && parsed.Sequence == 7, "Packet metadata lost");
        Reject(packet[..31], "Truncated header accepted");
        var invalid = (byte[])packet.Clone(); invalid[28] = 7; Reject(invalid, "Truncated payload accepted");
        invalid = (byte[])packet.Clone(); invalid[4] = 1; invalid[5] = 0; Reject(invalid, "Invalid dimensions accepted");
        invalid = (byte[])packet.Clone(); BinaryPrimitives.WriteInt64LittleEndian(invalid.AsSpan(16), long.MaxValue);
        Reject(invalid, "Timestamp overflow accepted");
        var path = Path.Combine(Path.GetTempPath(), "deskcam-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new DeviceStore(path);
            var token = store.Pair();
            Assert(store.Validate(token), "New pairing failed");
            Assert(new DeviceStore(path).Validate(token), "Paired device did not survive restart");
            Assert(!store.Validate(new string('0', 64)), "Wrong device token accepted");
            var second = store.Pair();
            Assert(!store.Validate(token) && store.Validate(second), "Re-pair did not revoke old credential");
            store.Revoke(); Assert(!store.Validate(second), "Revoked credential accepted");
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
        var photos = new PhotoStore();
        var controls = new CameraControlStore();
        try { controls.Request("zoom",2); throw new Exception("Offline command accepted"); } catch (InvalidOperationException) { }
        using var state = System.Text.Json.JsonDocument.Parse("{\"ready\":true,\"settings\":{\"zoom\":1}}");
        controls.Exchange(state.RootElement,0,null);
        var zoomCommand = controls.Request("zoom",2);
        Assert(controls.Exchange(state.RootElement,0,null) == zoomCommand, "Camera command not delivered");
        Assert(controls.Exchange(state.RootElement,0,null) is null, "Camera command replayed");
        Assert(controls.Snapshot.Pending, "Command completed without acknowledgement");
        controls.Exchange(state.RootElement,zoomCommand.Id+1,"wrong acknowledgement");
        Assert(controls.Snapshot.Pending, "Stale acknowledgement completed command");
        controls.Exchange(state.RootElement,zoomCommand.Id,"applied");
        Assert(!controls.Snapshot.Pending && controls.Snapshot.Result == "applied", "Acknowledgement lost");
        var request = photos.Request();
        Assert(photos.Request() == request && photos.TakeRequest() == request && photos.TakeRequest() is null,
            "Photo requests were duplicated or not consumed");
        // Minimal SOF header tests dimension parsing; real JPEG decoding is checked in desktop integration.
        byte[] jpeg = [0xff, 0xd8, 0xff, 0xc0, 0, 11, 8, 0x0b, 0xb8, 0x0f, 0xa0, 1, 1, 0x11, 0, 0xff, 0xd9];
        var photo = photos.Accept(jpeg, "camera-photo");
        Assert(photo.Width == 4000 && photo.Height == 3000 && photo.Sequence == 1 && photos.Latest == photo,
            "HD photo dimensions or sequence lost");
        foreach (var bad in new[] { jpeg[..10], new byte[32], new byte[] { 0xff, 0xd8, 0xff, 0xc0, 0, 1 } })
        {
            try { photos.Accept(bad, "camera-photo"); throw new Exception("Invalid photo accepted"); }
            catch (InvalidDataException) { }
        }
        Assert(photos.Latest == photo, "Invalid upload overwrote previous photo");
        var oversized = (byte[])jpeg.Clone(); oversized[9] = 0x30;
        try { PhotoStore.Dimensions(oversized); throw new Exception("Oversized dimensions accepted"); }
        catch (InvalidDataException) { }
        Console.WriteLine("DeskCam packet validation, persistent pairing and HD photo tests passed.");
    }
    private static void Reject(byte[] bytes, string reason)
    {
        try { VideoProtocol.Parse(bytes, 1); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException(reason);
    }
    private static void Assert(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
}
