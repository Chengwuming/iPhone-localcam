using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;

namespace LocalCam.Desktop;

public partial class PairingWindow : Window
{
    private readonly HttpClient httpClient = new();
    private bool loadingNetworks;

    public PairingWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadNetworksAsync();
        Closed += (_, _) => httpClient.Dispose();
    }

    private async Task LoadNetworksAsync()
    {
        try
        {
            loadingNetworks = true;
            var json = await httpClient.GetStringAsync("http://localhost:29100/api/networks");
            var networks = JsonSerializer.Deserialize<List<NetworkOption>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? [];
            NetworkSelector.ItemsSource = networks;
            NetworkSelector.SelectedItem = networks.FirstOrDefault(network => network.IsRecommended) ?? networks.FirstOrDefault();

            var phoneHotspot = networks.FirstOrDefault(network => network.IsPhoneWifiHotspot);
            var mobileHotspot = networks.FirstOrDefault(network => network.IsWindowsMobileHotspot);
            MobileHotspotHint.Text = phoneHotspot is not null
                ? $"当前正在使用手机热点（Wi-Fi）：{phoneHotspot.Name}（{phoneHotspot.Address}）。电脑与手机可通过此网络直接连接。"
                : mobileHotspot is null
                ? "未检测到 Windows 移动热点。需要同时上网时，推荐开启 iPhone“个人热点”并让 Windows 加入，然后点击“刷新”；也可打开 Windows 移动热点。VMware 等虚拟网卡已自动隐藏。"
                : $"已检测到 Windows 移动热点“{mobileHotspot.Name}”（{mobileHotspot.Address}）。让 iPhone 加入该热点后扫描下方二维码。";
        }
        catch (Exception exception)
        {
            ErrorText.Text = $"无法检测连接网络：{exception.Message}";
        }
        finally
        {
            loadingNetworks = false;
        }

        await LoadPairingAsync();
    }

    private async Task LoadPairingAsync()
    {
        try
        {
            if (NetworkSelector.SelectedItem is not NetworkOption selectedNetwork)
            {
                ErrorText.Text = "未检测到可用连接网络。请连接同一 Wi-Fi、手机热点、Windows 移动热点或 USB 网络后点击“刷新”。";
                return;
            }

            var json = await httpClient.GetStringAsync($"http://localhost:29100/api/session?address={Uri.EscapeDataString(selectedNetwork.Address)}");
            var pairing = JsonSerializer.Deserialize<PairingResponse>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? throw new InvalidOperationException("本地服务未返回配对信息。");
            PhoneQr.Source = FromDataUrl(pairing.PhoneQr);
            BootstrapQr.Source = FromDataUrl(pairing.BootstrapQr);
            ExpiryText.Text = $"二维码有效至 {pairing.ExpiresAt.LocalDateTime:t}";
            SelectedNetworkText.Text = $"当前二维码：{pairing.ConnectionType} · {pairing.ConnectionName}（{selectedNetwork.Address}）。同一 Wi-Fi 可直接连接；仅当该网络启用了客户端隔离时，才需要改用个人热点、Windows 移动热点或 USB。";
            ErrorText.Text = string.Empty;
        }
        catch (Exception exception)
        {
            ErrorText.Text = $"无法生成连接二维码：{exception.Message}";
        }
    }

    private async void RefreshNetworks_Click(object sender, RoutedEventArgs e) =>
        await LoadNetworksAsync();

    private async void NetworkSelector_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!loadingNetworks && NetworkSelector.SelectedItem is not null)
        {
            await LoadPairingAsync();
        }
    }

    private void OpenMobileHotspotSettings_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("ms-settings:network-mobilehotspot") { UseShellExecute = true });
    }

    private static BitmapImage FromDataUrl(string dataUrl)
    {
        var separator = dataUrl.IndexOf(',', StringComparison.Ordinal);
        if (separator < 0)
        {
            throw new InvalidOperationException("二维码格式无效。");
        }

        var image = new BitmapImage();
        using var stream = new MemoryStream(Convert.FromBase64String(dataUrl[(separator + 1)..]));
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private sealed record PairingResponse(
        string BootstrapQr,
        string PhoneQr,
        string ConnectionName,
        string ConnectionType,
        DateTimeOffset ExpiresAt);

    private sealed record NetworkOption(
        string Address,
        string Name,
        string ConnectionType,
        bool IsWindowsMobileHotspot,
        bool IsPhoneWifiHotspot,
        bool IsRecommended)
    {
        public string DisplayName => IsRecommended
            ? $"{Name} · {Address}（当前使用：{ConnectionType}）"
            : $"{Name} · {Address}（连接方式：{ConnectionType}）";
    }
}
