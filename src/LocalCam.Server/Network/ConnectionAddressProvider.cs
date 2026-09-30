using System.Net;
using System.Net.Sockets;

namespace LocalCam.Server.Network;

public sealed record ConnectionAddress(IPAddress Address, string Name, string ConnectionType,
    bool IsWindowsMobileHotspot = false, bool IsPhoneWifiHotspot = false, bool IsForwarded = false);

public static class ConnectionAddressProvider
{
    public const string ForwardedAddressFile = "deskcam-forwarded-address.txt";

    // Explicit router WAN configuration, not a detected PC adapter. Load at startup
    // so every advertised address is included in the server's TLS certificate.
    public static IPAddress? LoadForwardedAddress(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, ForwardedAddressFile);
        if (!File.Exists(path)) return null;
        var text = File.ReadAllText(path).Trim();
        if (text.Length == 0) return null;
        if (!IPAddress.TryParse(text, out var address) || address.AddressFamily != AddressFamily.InterNetwork ||
            IPAddress.IsLoopback(address) || address.GetAddressBytes() is [0 or >= 224, _, _, _] or [169, 254, _, _])
            throw new InvalidDataException($"{ForwardedAddressFile} 必须填写路由器的有效 IPv4 地址，不能填写网址或端口。");
        return address;
    }

    public static IReadOnlyList<ConnectionAddress> GetAddresses(IPAddress? forwardedAddress)
    {
        var local = LocalNetworkAddressProvider.GetUsableRoutes().Select(route => new ConnectionAddress(
            route.Address, route.Name, LocalNetworkAddressProvider.GetConnectionType(route),
            route.IsWindowsMobileHotspot, route.IsPhoneWifiHotspot));
        if (forwardedAddress is null) return local.ToArray();
        return new[] { new ConnectionAddress(forwardedAddress, "路由器校园网入口", "路由器端口转发", IsForwarded: true) }
            .Concat(local.Where(route => !route.Address.Equals(forwardedAddress))).ToArray();
    }
}
