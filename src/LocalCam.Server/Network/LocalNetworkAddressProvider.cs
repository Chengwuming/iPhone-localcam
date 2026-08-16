using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LocalCam.Server.Network;

public sealed record LocalNetworkRoute(
    IPAddress Address,
    string Name,
    string Description,
    bool IsWindowsMobileHotspot,
    bool IsAppleUsbTethering,
    bool IsPhoneWifiHotspot);

public static class LocalNetworkAddressProvider
{
    // Windows Internet Connection Sharing uses this address for Mobile hotspot by default.
    // Including it in the leaf certificate lets a hotspot be enabled after LocalCam starts.
    public static IPAddress WindowsMobileHotspotDefaultAddress { get; } = IPAddress.Parse("192.168.137.1");

    public static IReadOnlyList<IPAddress> GetUsableIPv4Addresses() =>
        GetUsableRoutes().Select(route => route.Address).ToArray();

    public static IReadOnlyList<LocalNetworkRoute> GetUsableRoutes()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up)
            .Where(network => network.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
            .SelectMany(network => network.GetIPProperties().UnicastAddresses
                .Select(unicast => CreateRoute(network, unicast)))
            .Where(route => route.Address.AddressFamily == AddressFamily.InterNetwork)
            .Where(route => !IPAddress.IsLoopback(route.Address) && !IsLinkLocal(route.Address))
            .Where(route => !IsExcludedVirtualOrTunnelAdapter(route))
            .DistinctBy(route => route.Address)
            .OrderByDescending(GetPriority)
            .ThenBy(route => route.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(route => route.Address.ToString(), StringComparer.Ordinal)
            .ToArray();
    }

    public static LocalNetworkRoute? GetPreferredRoute() => GetUsableRoutes().FirstOrDefault();

    public static string GetConnectionType(LocalNetworkRoute route) => route switch
    {
        { IsPhoneWifiHotspot: true } => "手机热点（Wi-Fi）",
        { IsWindowsMobileHotspot: true } => "Windows 移动热点",
        { IsAppleUsbTethering: true } => "iPhone USB",
        _ => "局域网"
    };

    private static LocalNetworkRoute CreateRoute(NetworkInterface network, UnicastIPAddressInformation unicast)
    {
        var address = unicast.Address;
        var appleUsbTethering = IsAppleUsbTethering(network);
        var windowsMobileHotspot = IsWindowsMobileHotspot(network, address);
        var phoneWifiHotspot = IsPhoneWifiHotspot(network, unicast);
        return new LocalNetworkRoute(address, network.Name, network.Description, windowsMobileHotspot, appleUsbTethering, phoneWifiHotspot);
    }

    private static int GetPriority(LocalNetworkRoute route) => route switch
    {
        { IsPhoneWifiHotspot: true } => 5,
        { IsWindowsMobileHotspot: true } => 4,
        { IsAppleUsbTethering: true } => 3,
        _ => 2
    };

    private static bool IsExcludedVirtualOrTunnelAdapter(LocalNetworkRoute route) =>
        route.Name.Contains("VMware", StringComparison.OrdinalIgnoreCase) ||
        route.Description.Contains("VMware", StringComparison.OrdinalIgnoreCase) ||
        route.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) ||
        route.Description.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase) ||
        route.Description.Contains("Virtual Ethernet", StringComparison.OrdinalIgnoreCase) ||
        route.Description.Contains("Tunnel", StringComparison.OrdinalIgnoreCase) ||
        route.Description.Contains("TAP", StringComparison.OrdinalIgnoreCase) ||
        route.Description.Contains("TUN", StringComparison.OrdinalIgnoreCase);

    private static bool IsLinkLocal(IPAddress address) =>
        address.GetAddressBytes() is [169, 254, _, _];

    private static bool IsAppleUsbTethering(NetworkInterface network) =>
        network.Description.Contains("Apple Mobile Device Ethernet", StringComparison.OrdinalIgnoreCase) ||
        network.Name.Contains("Apple Mobile Device Ethernet", StringComparison.OrdinalIgnoreCase);

    private static bool IsWindowsMobileHotspot(NetworkInterface network, IPAddress address) =>
        network.Description.Contains("Wi-Fi Direct Virtual Adapter", StringComparison.OrdinalIgnoreCase) &&
        IsPrivateIpv4(address);

    private static bool IsPhoneWifiHotspot(NetworkInterface network, UnicastIPAddressInformation unicast)
    {
        if (network.NetworkInterfaceType != NetworkInterfaceType.Wireless80211 ||
            unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
            unicast.PrefixLength != 28)
        {
            return false;
        }

        // iOS and several Android hotspot implementations use 172.20.10.0/28.
        // Treat it as a phone hotspot only on the active Wi-Fi adapter.
        return unicast.Address.GetAddressBytes() is [172, 20, 10, >= 2 and <= 14];
    }

    private static bool IsPrivateIpv4(IPAddress address)
    {
        var octets = address.GetAddressBytes();
        return octets is [10, _, _, _] or [192, 168, _, _] ||
               (octets[0] == 172 && octets[1] is >= 16 and <= 31);
    }
}
