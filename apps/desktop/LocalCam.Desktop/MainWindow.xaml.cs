using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LocalCam.Server;
using Point = System.Windows.Point;

namespace LocalCam.Desktop;
public partial class MainWindow : Window
{
    private readonly AppSettingsService settings;
    private readonly VideoPipeline? pipeline;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly WriteableBitmap bitmap = new(DisplayFrame.Width, DisplayFrame.Height, 96, 96, PixelFormats.Bgra32, null);
    private DisplayFrame? frozen;
    private long shownSequence, shownConnection, statusTick;
    private bool cropping, clean, dragging, started;
    private Point dragStart;
    private ViewTransform? dragTransform;
    private Rect contentRect;
    private HwndSource? hwndSource;
    private const int CopyHotkey = 0xDC01;
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);

    internal MainWindow(AppSettingsService settings, VideoPipeline? pipeline, string? cameraStatus, string? startupError)
    {
        this.settings = settings; this.pipeline = pipeline;
        InitializeComponent();
        CameraText.Text = cameraStatus ?? "虚拟摄像头未启动";
        if (startupError is not null) EmptyText.Text = startupError;
        Topmost = settings.Current.AlwaysOnTop;
        TopButton.Content = Topmost ? "取消置顶" : "置顶";
        if (settings.Current.WindowPlacement is { } p && p.Width >= 460 && p.Height >= 320 &&
            double.IsFinite(p.Left) && double.IsFinite(p.Top) && double.IsFinite(p.Width) && double.IsFinite(p.Height))
        {
            Width = Math.Min(p.Width, SystemParameters.VirtualScreenWidth);
            Height = Math.Min(p.Height, SystemParameters.VirtualScreenHeight);
            Left = Math.Clamp(p.Left, SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width);
            Top = Math.Clamp(p.Top, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height);
        }
        Closing += (_, e) => { Save(); ((App)System.Windows.Application.Current).HandleMainWindowClosing(e); };
        Closed += (_, _) =>
        {
            timer.Stop(); frozen?.Dispose();
            if (hwndSource is not null) { UnregisterHotKey(hwndSource.Handle, CopyHotkey); hwndSource.RemoveHook(WindowProc); }
        };
        SourceInitialized += (_, _) =>
        {
            hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            hwndSource.AddHook(WindowProc);
            if (!RegisterHotKey(hwndSource.Handle, CopyHotkey, 0x4000 | 0x2 | 0x1, 0x43))
                CameraText.Text += " · Ctrl+Alt+C 已被占用，窗口内 Ctrl+C 仍可复制";
        };
    }
    internal void Start()
    {
        if (started) return; started = true;
        timer.Tick += (_, _) => Tick(); timer.Start();
    }
    internal void EnterBackgroundMode() { Save(); Hide(); }
    internal void EnterForegroundMode() { }
    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == 0x0312 && w.ToInt32() == CopyHotkey) { Copy(); handled = true; }
        return IntPtr.Zero;
    }
    private void Tick()
    {
        using var live = pipeline?.Acquire();
        var frame = frozen ?? live;
        if (frame is not null)
        {
            if (live is not null && live.ConnectionId != shownConnection)
            {
                shownConnection = live.ConnectionId;
                Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            }
            if (frame.Sequence != shownSequence || PreviewImage.Source is null)
            {
                if (IsVisible)
                {
                    bitmap.WritePixels(new Int32Rect(0, 0, DisplayFrame.Width, DisplayFrame.Height), frame.Bgra, DisplayFrame.Width * 4, 0);
                    PreviewImage.Source = bitmap; Empty.Visibility = Visibility.Collapsed;
                    shownSequence = frame.Sequence;
                    contentRect = new Rect(frame.Content[0], frame.Content[1], frame.Content[2], frame.Content[3]);
                }
            }
        }
        else
        {
            PreviewImage.Source = null; Empty.Visibility = Visibility.Visible;
            shownSequence = 0;
        }
        if (Stopwatch.GetElapsedTime(statusTick).TotalSeconds >= 1)
        {
            statusTick = Stopwatch.GetTimestamp();
            StatusText.Text = pipeline?.Description ?? "视频模块未启动";
        }
    }
    private void Save()
    {
        try
        {
            var r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            settings.Save(settings.Current with
            {
                WindowPlacement = new(r.X, r.Y, r.Width, r.Height, false),
                View = pipeline?.Transform ?? settings.Current.View, AlwaysOnTop = Topmost
            });
        }
        catch (Exception ex) { StatusText.Text = "设置保存失败：" + ex.Message; }
    }
    private void Pair_Click(object sender, RoutedEventArgs e) => new PairingWindow { Owner = this }.ShowDialog();
    private void Settings_Click(object sender, RoutedEventArgs e) => new SettingsWindow(settings) { Owner = this }.ShowDialog();
    private void Resume() { frozen?.Dispose(); frozen = null; FrozenLabel.Visibility = Visibility.Collapsed; FreezeButton.Content = "冻结 Space"; }
    private void Rotate()
    {
        if (pipeline is null) return;
        Resume(); pipeline.Transform = new ViewTransform((pipeline.Transform.Rotation + 1) % 4); Save();
    }
    private void Rotate_Click(object sender, RoutedEventArgs e) => Rotate();
    private void Crop_Click(object sender, RoutedEventArgs e) => ToggleCrop();
    private void ToggleCrop() { cropping = !cropping; CropButton.Content = cropping ? "拖动框选区域" : "裁剪 C"; }
    private void Reset_Click(object sender, RoutedEventArgs e) => Reset();
    private void Reset() { if (pipeline is null) return; Resume(); pipeline.Transform = new ViewTransform(pipeline.Transform.Rotation); Save(); }
    private void Freeze_Click(object sender, RoutedEventArgs e) => Freeze();
    private void Freeze()
    {
        if (frozen is not null) { Resume(); return; }
        frozen = pipeline?.Acquire();
        if (frozen is not null) { FrozenLabel.Visibility = clean ? Visibility.Collapsed : Visibility.Visible; FreezeButton.Content = "恢复 Space"; }
    }
    private void Copy_Click(object sender, RoutedEventArgs e) => Copy();
    private void Copy()
    {
        using var live = frozen is null ? pipeline?.Acquire() : null;
        var frame = frozen ?? live;
        if (frame is null) { StatusText.Text = "还没有可复制的画面"; return; }
        try { System.Windows.Clipboard.SetImage(frame.ToBitmap(true)); StatusText.Text = "已复制当前画面，可直接粘贴"; statusTick = Stopwatch.GetTimestamp() + Stopwatch.Frequency; }
        catch (ExternalException) { StatusText.Text = "剪贴板正忙，请再按一次 Ctrl+C"; }
    }
    private void Top_Click(object sender, RoutedEventArgs e) { Topmost = !Topmost; TopButton.Content = Topmost ? "取消置顶" : "置顶"; Save(); }
    private void Clean_Click(object sender, RoutedEventArgs e) => Clean();
    private void Clean()
    {
        clean = !clean;
        Header.Visibility = Controls.Visibility = clean ? Visibility.Collapsed : Visibility.Visible;
        FrozenLabel.Visibility = !clean && frozen is not null ? Visibility.Visible : Visibility.Collapsed;
        WindowStyle = clean ? WindowStyle.None : WindowStyle.SingleBorderWindow;
        ResizeMode = clean ? ResizeMode.CanResizeWithGrip : ResizeMode.CanResize;
    }
    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) Copy();
        else if (e.Key == Key.C) ToggleCrop();
        else if (e.Key == Key.R) Rotate();
        else if (e.Key is Key.D0 or Key.NumPad0) Reset();
        else if (e.Key == Key.Space) Freeze();
        else if (e.Key == Key.F11 || (e.Key == Key.Escape && clean)) Clean();
        else return;
        e.Handled = true;
    }
    private Point OutputPoint(Point p)
    {
        var scale = Math.Min(Viewport.ActualWidth / DisplayFrame.Width, Viewport.ActualHeight / DisplayFrame.Height);
        if (scale <= 0) return new Point();
        return new Point((p.X - (Viewport.ActualWidth - DisplayFrame.Width * scale) / 2) / scale,
            (p.Y - (Viewport.ActualHeight - DisplayFrame.Height * scale) / 2) / scale);
    }
    private Point Fraction(Point p)
    {
        var q = OutputPoint(p);
        return new Point(Math.Clamp((q.X - contentRect.X) / Math.Max(1, contentRect.Width), 0, 1),
            Math.Clamp((q.Y - contentRect.Y) / Math.Max(1, contentRect.Height), 0, 1));
    }
    private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (pipeline is null || PreviewImage.Source is null) return;
        Resume();
        var t = pipeline.Transform; var anchor = Fraction(e.GetPosition(Viewport));
        double factor = e.Delta > 0 ? .9 : 1 / .9, w = Math.Clamp(t.Width * factor, .05, 1), h = Math.Clamp(t.Height * factor, .05, 1);
        pipeline.Transform = t with { X = t.X + anchor.X * (t.Width - w), Y = t.Y + anchor.Y * (t.Height - h), Width = w, Height = h };
        Save(); e.Handled = true;
    }
    private void Viewport_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (PreviewImage.Source is null || pipeline is null) return;
        if (clean && e.ClickCount == 2) { Clean(); return; }
        Resume(); dragging = true; dragStart = e.GetPosition(Viewport); dragTransform = pipeline.Transform;
        Viewport.CaptureMouse();
        if (cropping) { Selection.Visibility = Visibility.Visible; Selection.Width = Selection.Height = 0; }
    }
    private void Viewport_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!dragging || dragTransform is null || pipeline is null) return;
        var now = e.GetPosition(Viewport);
        if (cropping)
        {
            System.Windows.Controls.Canvas.SetLeft(Selection, Math.Min(now.X, dragStart.X));
            System.Windows.Controls.Canvas.SetTop(Selection, Math.Min(now.Y, dragStart.Y));
            Selection.Width = Math.Abs(now.X - dragStart.X); Selection.Height = Math.Abs(now.Y - dragStart.Y);
        }
        else
        {
            var a = OutputPoint(dragStart); var b = OutputPoint(now);
            pipeline.Transform = dragTransform with
            {
                X = dragTransform.X - (b.X - a.X) / Math.Max(1, contentRect.Width) * dragTransform.Width,
                Y = dragTransform.Y - (b.Y - a.Y) / Math.Max(1, contentRect.Height) * dragTransform.Height
            };
        }
    }
    private void Viewport_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!dragging || dragTransform is null || pipeline is null) return;
        dragging = false; Viewport.ReleaseMouseCapture(); Selection.Visibility = Visibility.Collapsed;
        if (cropping)
        {
            var a = Fraction(dragStart); var b = Fraction(e.GetPosition(Viewport));
            double w = Math.Abs(a.X - b.X), h = Math.Abs(a.Y - b.Y);
            if (w >= .05 && h >= .05)
                pipeline.Transform = dragTransform with
                {
                    X = dragTransform.X + Math.Min(a.X, b.X) * dragTransform.Width,
                    Y = dragTransform.Y + Math.Min(a.Y, b.Y) * dragTransform.Height,
                    Width = dragTransform.Width * w, Height = dragTransform.Height * h
                };
            ToggleCrop();
        }
        Save();
    }
}
