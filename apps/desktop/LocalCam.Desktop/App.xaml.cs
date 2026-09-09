using System.Windows;
using LocalCam.Server;

namespace LocalCam.Desktop;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = @"Local\LocalCam.Singleton";
    private const string ShowRequestEventName = @"Local\LocalCam.Show";
    private readonly CancellationTokenSource lifetime = new();
    private readonly AppSettingsService settingsService = new();
    private Mutex? singleInstanceMutex;
    private EventWaitHandle? showRequestEvent;
    private Task? showRequestTask;
    private TrayIconService? trayIconService;
    private VirtualCameraSessionService? virtualCameraSession;
    private LocalCamServerInstance? server;
    private VideoPipeline? video;
    private bool exitRequested;
    private bool ownsSingleInstanceMutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out ownsSingleInstanceMutex);
        if (!ownsSingleInstanceMutex)
        {
            try
            {
                using var existingEvent = EventWaitHandle.OpenExisting(ShowRequestEventName);
                existingEvent.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }

            Shutdown();
            return;
        }

        showRequestEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowRequestEventName);
        settingsService.Load();

        string? startupError = null;
        try
        {
            server = await LocalCamServerHost.StartAsync(cancellationToken: lifetime.Token);
            video = new VideoPipeline(server.Relay, settingsService.Current.View ?? new ViewTransform());
            server.Snapshot = video.Snapshot;
            server.VideoStatus = () => video.Status;
        }
        catch (Exception exception) when (!lifetime.IsCancellationRequested)
        {
            startupError = $"本地连接服务启动失败：{exception.Message}";
        }

        virtualCameraSession = new VirtualCameraSessionService();
        try
        {
            await virtualCameraSession.StartAsync(lifetime.Token);
        }
        catch (Exception exception) when (!lifetime.IsCancellationRequested)
        {
            virtualCameraSession.Dispose();
            virtualCameraSession = null;
            startupError = string.IsNullOrWhiteSpace(startupError)
                ? $"虚拟摄像头启动失败：{exception.Message}"
                : $"{startupError}\n虚拟摄像头启动失败：{exception.Message}";
        }

        var mainWindow = new MainWindow(settingsService, video, virtualCameraSession?.StatusText, startupError, server?.Photos, server?.Camera);
        MainWindow = mainWindow;
        trayIconService = new TrayIconService(mainWindow, settingsService, RequestExit);
        showRequestTask = ListenForShowRequestsAsync(lifetime.Token);
        mainWindow.Start();
        if (e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase))
        {
            mainWindow.EnterBackgroundMode();
        }
        else
        {
            mainWindow.EnterForegroundMode();
            mainWindow.Show();
        }

        if (e.Args.Contains("--show-tray-menu", StringComparer.OrdinalIgnoreCase))
        {
            trayIconService.ShowMenuForDiagnostics();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        lifetime.Cancel();
        if (showRequestTask is not null)
        {
            try
            {
                showRequestTask.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
        }
        showRequestEvent?.Dispose();
        trayIconService?.Dispose();
        virtualCameraSession?.Dispose();
        video?.Dispose();
        if (server is not null)
        {
            try
            {
                Task.Run(async () =>
                {
                    await server.StopAsync().ConfigureAwait(false);
                    await server.DisposeAsync().ConfigureAwait(false);
                }).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
        }
        if (ownsSingleInstanceMutex)
        {
            singleInstanceMutex?.ReleaseMutex();
        }
        singleInstanceMutex?.Dispose();
        lifetime.Dispose();
        base.OnExit(e);
    }

    internal void HandleMainWindowClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (exitRequested)
        {
            return;
        }

        e.Cancel = true;
        if (settingsService.Current.CloseBehavior == CloseBehavior.MinimizeToTray)
        {
            if (MainWindow is MainWindow mainWindow)
            {
                mainWindow.EnterBackgroundMode();
            }
            return;
        }

        RequestExit();
    }

    internal void RequestExit()
    {
        if (exitRequested)
        {
            return;
        }

        exitRequested = true;
        Shutdown();
    }

    private Task ListenForShowRequestsAsync(CancellationToken cancellationToken)
    {
        var requestEvent = showRequestEvent
            ?? throw new InvalidOperationException("LocalCam show request event is not initialized.");
        return Task.Run(() =>
        {
            var handles = new WaitHandle[] { requestEvent, cancellationToken.WaitHandle };
            while (WaitHandle.WaitAny(handles) == 0)
            {
                _ = Dispatcher.BeginInvoke(() => trayIconService?.ShowMainWindow());
            }
        }, cancellationToken);
    }
}
