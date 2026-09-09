using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace LocalCam.Server.Pairing;
// Only one paired phone; persist its credential hash, not its bearer token.
public sealed class DeviceStore(string directory)
{
    private readonly object gate = new();
    private readonly string path = Path.Combine(directory, "deskcam-device.json");
    public string Pair()
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        lock (gate)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new { hash = Hash(token) }));
            File.Move(path + ".tmp", path, true);
        }
        return token;
    }
    public bool Validate(string? token)
    {
        if (token is null || token.Length != 64) return false;
        lock (gate)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var hash = doc.RootElement.GetProperty("hash").GetString();
                return hash is not null && CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(Hash(token)));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return false; }
        }
    }
    public void Revoke() { lock (gate) { if (File.Exists(path)) File.Delete(path); } }
    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
