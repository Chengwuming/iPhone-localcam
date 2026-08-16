using System.Drawing;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace LocalCam.Desktop;

internal sealed class TrayIconService : IDisposable
{
    private readonly MainWindow mainWindow;
    private readonly AppSettingsService settingsService;
    private readonly Action exitAction;
    private readonly Forms.NotifyIcon notifyIcon;
    private readonly Icon icon;
    private TrayMenuWindow? trayMenu;

    public TrayIconService(MainWindow mainWindow, AppSettingsService settingsService, Action exitAction)
    {
        this.mainWindow = mainWindow;
        this.settingsService = settingsService;
        this.exitAction = exitAction;
        icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? throw new InvalidOperationException("无法获取 LocalCam 可执行文件路径。"))
            ?? throw new InvalidOperationException("无法加载 LocalCam 图标。");

        notifyIcon = new Forms.NotifyIcon
        {
            Icon = icon,
            Text = "LocalCam - 本地 iPhone 摄像头",
            Visible = true
        };
        notifyIcon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                Dispatch(RestoreWindow);
            }
        };
        notifyIcon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Right)
            {
                Dispatch(OpenContextMenu);
            }
        };
    }

    public void Dispose()
    {
        trayMenu?.Close();
        notifyIcon.Visible = false;
        notifyIcon.Dispose();
        icon.Dispose();
    }

    internal void ShowMenuForDiagnostics() =>
        _ = mainWindow.Dispatcher.BeginInvoke(
            () => OpenContextMenuCore(true),
            DispatcherPriority.ApplicationIdle);

    internal void ShowMainWindow() => Dispatch(RestoreWindow);

    private void Dispatch(Action action)
    {
        if (mainWindow.Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _ = mainWindow.Dispatcher.BeginInvoke(action);
        }
    }

    private void OpenContextMenu()
    {
        OpenContextMenuCore(false);
    }

    private void OpenContextMenuCore(bool diagnostic)
    {
        if (trayMenu is { IsVisible: true })
        {
            trayMenu.Activate();
            return;
        }

        trayMenu = new TrayMenuWindow(RestoreWindow, ShowPairing, ShowSettings, exitAction);
        trayMenu.ShowInTaskbar = diagnostic;
        trayMenu.Closed += (_, _) => trayMenu = null;
        trayMenu.Show();
        trayMenu.Activate();
    }

    private void RestoreWindow()
    {
        mainWindow.EnterForegroundMode();
        if (mainWindow.WindowState == WindowState.Minimized)
        {
            mainWindow.WindowState = WindowState.Normal;
        }

        mainWindow.Show();
        mainWindow.Activate();
    }

    private void ShowPairing()
    {
        RestoreWindow();
        new PairingWindow { Owner = mainWindow }.ShowDialog();
    }

    private void ShowSettings()
    {
        RestoreWindow();
        new SettingsWindow(settingsService) { Owner = mainWindow }.ShowDialog();
    }
}
