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
        Console.WriteLine("DeskCam packet validation and persistent pairing tests passed.");
    }
    private static void Reject(byte[] bytes, string reason)
    {
        try { VideoProtocol.Parse(bytes, 1); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException(reason);
    }
    private static void Assert(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
}
