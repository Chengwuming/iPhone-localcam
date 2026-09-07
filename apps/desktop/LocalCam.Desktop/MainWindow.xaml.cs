using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LocalCam.Desktop;

public partial class MainWindow : Window
{
    private readonly AppSettingsService settingsService;
    private readonly FrameRingProducer frameProducer = new();
    private readonly HttpClient statusClient = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim controlSendLock = new(1, 1);
    private readonly DispatcherTimer zoomTimer = new() { Interval = TimeSpan.FromMilliseconds(75) };
    private ClientWebSocket? monitorSocket;
    private int framesSinceLastMeasure;
    private DateTimeOffset lastMeasure = DateTimeOffset.UtcNow;
    private double? pendingZoom;
    private bool sendingZoom;
    private bool hasSeenPhone;
    private bool previewMirrored;
    private bool started;
    private bool backgroundMode;
    private bool hasBeenShown;
    private bool? lastReportedPhoneConnection;
    private bool statusUnavailable;
    private int backgroundTransitionVersion;

    internal MainWindow(AppSettingsService settingsService, string? virtualCameraStatus, string? startupError)
    {
        this.settingsService = settingsService;
        InitializeComponent();
        RestoreWindowPlacement();
        VirtualCameraStatusText.Text = virtualCameraStatus ?? "虚拟摄像头未能随软件启动";
        if (!string.IsNullOrWhiteSpace(startupError))
        {
            ConnectionStatus.Foreground = System.Windows.Media.Brushes.IndianRed;
            ConnectionStatus.Text = startupError;
        }
        zoomTimer.Tick += ZoomTimer_Tick;
        Closing += (_, e) => ((App)System.Windows.Application.Current).HandleMainWindowClosing(e);
        Closed += (_, _) =>
        {
            SaveWindowPlacement();
            lifetime.Cancel();
            zoomTimer.Stop();
            frameProducer.Dispose();
            statusClient.Dispose();
            controlSendLock.Dispose();
            lifetime.Dispose();
        };
    }

    internal void Start()
    {
        if (started)
        {
            return;
        }

        started = true;
        _ = RunMonitorConnectionLoopAsync(lifetime.Token);
        _ = MonitorPhoneStatusAsync(lifetime.Token);
    }

    internal void EnterBackgroundMode()
    {
        if (IsVisible)
        {
            SaveWindowPlacement();
        }

        backgroundMode = true;
        Interlocked.Increment(ref backgroundTransitionVersion);
        zoomTimer.Stop();
        pendingZoom = null;
        PreviewImage.Source = null;
        Metrics.Text = "后台运行中，预览已卸载";
        EmptyPreview.Visibility = Visibility.Visible;
        Hide();
        ScheduleBackgroundMemoryTrim();
    }

    internal void EnterForegroundMode()
    {
        hasBeenShown = true;
        backgroundMode = false;
        Interlocked.Increment(ref backgroundTransitionVersion);
    }

    private async Task RunMonitorConnectionLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var socket = new ClientWebSocket();
            try
            {
                await socket.ConnectAsync(new Uri("ws://localhost:29100/ws/monitor"), cancellationToken);
                monitorSocket = socket;
                await Dispatcher.InvokeAsync(() =>
                {
                    ConnectionStatus.Foreground = System.Windows.Media.Brushes.DimGray;
                    ConnectionStatus.Text = "正在等待 iPhone";
                });
                await ReceiveFramesAsync(socket, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    DeviceStatusText.Text = "本地服务不可用，正在重试";
                    ConnectionStatus.Foreground = System.Windows.Media.Brushes.IndianRed;
                    ConnectionStatus.Text = "本地连接服务暂时不可用，正在自动重连…";
                    ClearPreview();
                });
            }
            finally
            {
                if (ReferenceEquals(monitorSocket, socket))
                {
                    monitorSocket = null;
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ReceiveFramesAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        using var data = new MemoryStream(256 * 1024);
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            data.Position = 0;
            data.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                await data.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);
            }
            while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Binary || data.Length == 0)
            {
                continue;
            }

            data.Position = 0;
            var source = FromJpeg(data, backgroundMode ? 720 : null);
            await Dispatcher.InvokeAsync(() =>
            {
                frameProducer.Publish(source, previewMirrored);
                if (backgroundMode)
                {
                    return;
                }

                PreviewImage.Source = source;
                EmptyPreview.Visibility = Visibility.Collapsed;
                DeviceStatusText.Text = "已连接并正在传输";
                ConnectionStatus.Text = "正在接收 iPhone 视频";
                UpdateMetrics();
            });
        }
    }

    private async Task MonitorPhoneStatusAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(
                backgroundMode ? TimeSpan.FromSeconds(2) : TimeSpan.FromMilliseconds(500),
                cancellationToken);
            try
            {
                var status = await statusClient.GetFromJsonAsync<ServerStatus>(
                    "http://localhost:29100/api/status",
                    cancellationToken);
                if (status is null)
                {
                    continue;
                }

                statusUnavailable = false;
                if (lastReportedPhoneConnection != status.PhoneConnected)
                {
                    lastReportedPhoneConnection = status.PhoneConnected;
                    await Dispatcher.InvokeAsync(() => UpdatePhoneStatus(status.PhoneConnected));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                if (statusUnavailable)
                {
                    continue;
                }

                statusUnavailable = true;
                await Dispatcher.InvokeAsync(() =>
                {
                    frameProducer.Clear();
                    if (backgroundMode)
                    {
                        return;
                    }

                    DeviceStatusText.Text = "本地服务不可用";
                    ConnectionStatus.Foreground = System.Windows.Media.Brushes.IndianRed;
                    ConnectionStatus.Text = "与 LocalCam 本地服务的连接已断开";
                    ClearPreview();
                });
            }
        }
    }

    private void UpdatePhoneStatus(bool connected)
    {
        if (connected)
        {
            hasSeenPhone = true;
            if (backgroundMode)
            {
                return;
            }

            DeviceStatusText.Text = PreviewImage.Source is null ? "已连接，等待视频帧" : "已连接并正在传输";
            ConnectionStatus.Foreground = System.Windows.Media.Brushes.ForestGreen;
            if (PreviewImage.Source is null)
            {
                ConnectionStatus.Text = "iPhone 已连接，等待视频帧";
            }
            return;
        }

        frameProducer.Clear();
        if (backgroundMode)
        {
            return;
        }

        DeviceStatusText.Text = hasSeenPhone ? "已断开" : "等待连接";
        ConnectionStatus.Foreground = hasSeenPhone
            ? System.Windows.Media.Brushes.IndianRed
            : System.Windows.Media.Brushes.DimGray;
        ConnectionStatus.Text = hasSeenPhone ? "iPhone 已断开" : "正在等待 iPhone";
        ClearPreview();
    }

    private void ClearPreview()
    {
        frameProducer.Clear();
        PreviewImage.Source = null;
        if (backgroundMode)
        {
            return;
        }

        EmptyPreview.Visibility = Visibility.Visible;
        Metrics.Text = "尚未收到视频帧";
        framesSinceLastMeasure = 0;
        lastMeasure = DateTimeOffset.UtcNow;
    }

    private void UpdateMetrics()
    {
        framesSinceLastMeasure++;
        var now = DateTimeOffset.UtcNow;
        var elapsed = now - lastMeasure;
        if (elapsed < TimeSpan.FromSeconds(1))
        {
            return;
        }

        Metrics.Text = $"JPEG 验证通道：约 {framesSinceLastMeasure / elapsed.TotalSeconds:F0} 帧/秒";
        framesSinceLastMeasure = 0;
        lastMeasure = now;
    }

    private async Task SendControlAsync(object control)
    {
        var socket = monitorSocket;
        if (socket?.State != WebSocketState.Open)
        {
            ConnectionStatus.Text = "没有可控制的 iPhone 连接";
            return;
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(control);
        await controlSendLock.WaitAsync(lifetime.Token);
        try
        {
            await socket.SendAsync(json, WebSocketMessageType.Text, true, lifetime.Token);
        }
        finally
        {
            controlSendLock.Release();
        }
    }

    private async void RearCamera_Click(object sender, RoutedEventArgs e) =>
        await SendControlAsync(new { type = "camera.switch", facing = "environment" });

    private async void FrontCamera_Click(object sender, RoutedEventArgs e) =>
        await SendControlAsync(new { type = "camera.switch", facing = "user" });

    private async void Fps20_Click(object sender, RoutedEventArgs e) =>
        await SendControlAsync(new { type = "capture.fps", fps = 20 });

    private async void Fps30_Click(object sender, RoutedEventArgs e) =>
        await SendControlAsync(new { type = "capture.fps", fps = 30 });

    private void Fit_Click(object sender, RoutedEventArgs e) => PreviewImage.Stretch = Stretch.Uniform;

    private void Fill_Click(object sender, RoutedEventArgs e) => PreviewImage.Stretch = Stretch.UniformToFill;

    private void Mirror_Click(object sender, RoutedEventArgs e)
    {
        previewMirrored = !previewMirrored;
        PreviewImage.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
        PreviewImage.RenderTransform = previewMirrored
            ? new ScaleTransform(-1, 1)
            : Transform.Identity;
        MirrorButton.Content = previewMirrored ? "摄像头镜像：开启" : "摄像头镜像：关闭";
    }

    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ZoomText is not null)
        {
            ZoomText.Text = $"{e.NewValue:F1}×";
        }

        if (!IsLoaded)
        {
            return;
        }

        pendingZoom = Math.Round(e.NewValue, 1);
        if (!zoomTimer.IsEnabled)
        {
            zoomTimer.Start();
        }
    }

    private async void ZoomTimer_Tick(object? sender, EventArgs e)
    {
        if (sendingZoom || pendingZoom is not double zoom)
        {
            if (pendingZoom is null)
            {
                zoomTimer.Stop();
            }
            return;
        }

        pendingZoom = null;
        sendingZoom = true;
        try
        {
            await SendControlAsync(new { type = "camera.zoom", zoom });
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            zoomTimer.Stop();
        }
        finally
        {
            sendingZoom = false;
            if (pendingZoom is null)
            {
                zoomTimer.Stop();
            }
        }
    }

    private void ConnectIphone_Click(object sender, RoutedEventArgs e) =>
        new PairingWindow { Owner = this }.ShowDialog();

    private void Settings_Click(object sender, RoutedEventArgs e) =>
        new SettingsWindow(settingsService) { Owner = this }.ShowDialog();

    private void ConnectionHelp_Click(object sender, RoutedEventArgs e) =>
        System.Windows.MessageBox.Show(
            "校园网无法互访时，推荐开启 iPhone“个人热点”，让 Windows 加入后回到 LocalCam 点击“刷新”；这样手机和电脑通常都能继续上网。也可以使用 Windows 移动热点，但代理/VPN 的 TUN 模式可能与 Windows 网络共享冲突。\n\n首次使用：展开证书引导，完成 iPhone 的证书安装和完全信任。\n\n日常使用：确认二维码中的网络地址与当前连接一致，再扫描二维码。USB 插入不会自动唤起 Safari。",
            "LocalCam 本地连接说明",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

    private static BitmapImage FromJpeg(Stream stream, int? decodePixelWidth)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        if (decodePixelWidth is int width)
        {
            image.DecodePixelWidth = width;
        }
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void RestoreWindowPlacement()
    {
        var placement = settingsService.Current.WindowPlacement;
        if (placement is null || !IsValidPlacement(placement))
        {
            return;
        }

        var virtualBounds = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        var savedBounds = new Rect(placement.Left, placement.Top, placement.Width, placement.Height);
        if (!virtualBounds.IntersectsWith(savedBounds))
        {
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = Math.Clamp(placement.Width, MinWidth, Math.Max(MinWidth, virtualBounds.Width));
        Height = Math.Clamp(placement.Height, MinHeight, Math.Max(MinHeight, virtualBounds.Height));
        Left = Math.Clamp(placement.Left, virtualBounds.Left - Width + 96, virtualBounds.Right - 96);
        Top = Math.Clamp(placement.Top, virtualBounds.Top, virtualBounds.Bottom - 48);
        if (placement.WasMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void SaveWindowPlacement()
    {
        if (!hasBeenShown)
        {
            return;
        }

        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;
        if (bounds.IsEmpty || !IsFinite(bounds.Left) || !IsFinite(bounds.Top) ||
            !IsFinite(bounds.Width) || !IsFinite(bounds.Height) || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        settingsService.SaveWindowPlacement(new WindowPlacementSettings(
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            WindowState == WindowState.Maximized));
    }

    private void ScheduleBackgroundMemoryTrim()
    {
        var version = Volatile.Read(ref backgroundTransitionVersion);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), lifetime.Token).ConfigureAwait(false);
                if (version == Volatile.Read(ref backgroundTransitionVersion))
                {
                    BackgroundMemoryManager.TrimAfterUiRelease();
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private static bool IsValidPlacement(WindowPlacementSettings placement) =>
        IsFinite(placement.Left) && IsFinite(placement.Top) &&
        IsFinite(placement.Width) && IsFinite(placement.Height) &&
        placement.Width >= 320 && placement.Height >= 240;

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private sealed record ServerStatus(bool PhoneConnected);
}
