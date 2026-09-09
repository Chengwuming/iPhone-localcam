using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LocalCam.Server;
using LocalCam.Server.Streaming;
using System.Net.Http;
using Point = System.Windows.Point;

namespace LocalCam.Desktop;
public partial class MainWindow : Window
{
    private readonly AppSettingsService settings;
    private readonly VideoPipeline? pipeline;
    private readonly PhotoStore? photos;
    private readonly CameraControlStore? camera;
    private CameraPanel? cameraPanel;
    private PhotoWindow? photoWindow;
    private bool inspecting;
    private Point inspectPoint = new(.5,.5);
    private long lastInspect;
    private long shownPhoto;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private WriteableBitmap? bitmap;
    private int displayWidth = 1920, displayHeight = 1080;
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

    internal MainWindow(AppSettingsService settings, VideoPipeline? pipeline, string? cameraStatus, string? startupError, PhotoStore? photos = null, CameraControlStore? camera = null)
    {
        this.settings = settings; this.pipeline = pipeline; this.photos = photos; this.camera = camera;
        InitializeComponent();
        if(camera is not null){cameraPanel=new CameraPanel(camera,settings,()=>pipeline?.Transform??new ViewTransform(),v=>{Resume();if(pipeline is not null)pipeline.Transform=v;Save();});CameraSidebar.Content=cameraPanel;}
        SizeChanged+=(_,_)=>UpdateSidebar();UpdateSidebar();
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
            timer.Stop(); frozen?.Dispose(); cameraPanel?.Dispose();
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
        if (camera?.Snapshot is { } cameraState)
        {
            bool ready = DateTimeOffset.UtcNow-cameraState.UpdatedAt<TimeSpan.FromSeconds(3) && !cameraState.Pending &&
                cameraState.State is {} cs && cs.TryGetProperty("ready",out var rd) && rd.ValueKind==System.Text.Json.JsonValueKind.True &&
                (!cs.TryGetProperty("busy",out var busy)||busy.ValueKind!=System.Text.Json.JsonValueKind.True);
            bool locked = cameraState.State is {} ls && ls.TryGetProperty("focusLocked",out var lk) && lk.ValueKind==System.Text.Json.JsonValueKind.True;
            bool canLock = cameraState.State is {} fs && fs.TryGetProperty("canLock",out var cl) && cl.ValueKind==System.Text.Json.JsonValueKind.True;
            AutofocusButton.IsEnabled=ready; FocusLockButton.IsEnabled=ready&&(locked||canLock);
            FocusLockButton.Content=locked?"焦点已锁定 L":"锁定焦点 L";
            FocusLockButton.ToolTip=canLock||locked?"L 切换锁焦 / 自动；不会冻结预览":"手机未开放锁焦；Space 可冻结预览，但不是锁定相机焦点";
            CaptureText.Text = cameraState.Pending ? cameraState.Result : cameraState.State is { } state &&
                state.TryGetProperty("photoStatus", out var message) ? message.GetString() : "";
            if (!cameraState.Pending && (cameraState.Result.StartsWith("操作失败") || cameraState.Result.StartsWith("手机未完成")))
                CaptureText.Text += " · " + cameraState.Result;
        }
        if (photos?.Latest is { } photo && photo.Sequence != shownPhoto)
        {
            shownPhoto = photo.Sequence;
            try { ShowPhoto(photo); }
            catch (Exception ex) { StatusText.Text = "照片打开失败：" + ex.Message; }
        }
        if(camera is null)FocusLockButton.IsEnabled=AutofocusButton.IsEnabled=false;
        CaptureText.Visibility = camera?.Snapshot.Pending==true || photos?.Latest is not null || CaptureText.Text.Contains("失败") ? Visibility.Visible : Visibility.Collapsed;
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
                    if (bitmap is null || bitmap.PixelWidth != frame.Width || bitmap.PixelHeight != frame.Height)
                        bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
                    displayWidth = frame.Width; displayHeight = frame.Height;
                    bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Bgra, frame.Width * 4, 0);
                    PreviewImage.Source = bitmap; Empty.Visibility = Visibility.Collapsed;
                    shownSequence = frame.Sequence;
                    contentRect = new Rect(frame.Content[0], frame.Content[1], frame.Content[2], frame.Content[3]);

                }
            }
            if(IsVisible&&bitmap is not null&&inspecting&&!clean&&Stopwatch.GetElapsedTime(lastInspect).TotalMilliseconds>=150){
                lastInspect=Stopwatch.GetTimestamp();
                int w=Math.Min(256,frame.Width),h=Math.Min(150,frame.Height);
                var box=new Int32Rect(Math.Clamp((int)(inspectPoint.X*frame.Width)-w/2,0,frame.Width-w),Math.Clamp((int)(inspectPoint.Y*frame.Height)-h/2,0,frame.Height-h),w,h);
                InspectImage.Source=new CroppedBitmap(bitmap,box);
                var dpi=VisualTreeHelper.GetDpi(this);InspectImage.Width=w/dpi.DpiScaleX;InspectImage.Height=h/dpi.DpiScaleY;
                InspectImage.Stretch=Stretch.Fill;
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
    private async void CaptureHD_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await client.PostAsync("http://127.0.0.1:29100/api/photo/request", null);
            response.EnsureSuccessStatusCode();
            StatusText.Text = "已请求手机拍照；若未出现照片，请查看手机提示或使用系统相机拍照";
            statusTick = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 8;
        }
        catch (Exception ex) { StatusText.Text = "拍照请求失败：" + ex.Message; statusTick = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 3; }
    }
    private void Settings_Click(object sender, RoutedEventArgs e) => new SettingsWindow(settings) { Owner = this }.ShowDialog();
    private void CameraControls_Click(object sender, RoutedEventArgs e)
    {
        if(camera is null)return;
        bool show=CameraSidebar.Visibility!=Visibility.Visible;
        settings.Save(settings.Current with{CameraSidebar=show});
        if(show&&ActualWidth<850)Width=Math.Min(1000,SystemParameters.WorkArea.Width);
        UpdateSidebar();
    }
    private void UpdateSidebar(){CameraSidebar.Visibility=!clean&&settings.Current.CameraSidebar&&ActualWidth>=850?Visibility.Visible:Visibility.Collapsed;SecondaryActions.IsExpanded=ActualWidth>=700;}
    private void Inspect_Click(object sender,RoutedEventArgs e){inspecting=!inspecting;InspectBox.Visibility=inspecting&&!clean?Visibility.Visible:Visibility.Collapsed;InspectButton.Content=inspecting?"关闭细字检查 I":"细字检查 I";}
    private void ShowPhoto(CapturedPhoto photo){photoWindow?.Close();photoWindow=new PhotoWindow(photo){Owner=this};photoWindow.Closed+=(_,_)=>photoWindow=null;Show();photoWindow.Show();}
    private void RecentPhoto_Click(object sender,RoutedEventArgs e){if(photos?.Latest is {} photo)ShowPhoto(photo);else{StatusText.Text="还没有成功抓拍的图片";statusTick=Stopwatch.GetTimestamp()+Stopwatch.Frequency*3;}}
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
    private void SaveFrame_Click(object sender,RoutedEventArgs e) => SaveFrame();
    private void SaveFrame()
    {
        using var live=frozen is null?pipeline?.Acquire():null;
        var frame=frozen??live;
        if(frame is null){StatusText.Text="还没有可保存的画面";return;}
        // Capture before opening the dialog, so the saved image is the frame the user chose.
        var pixels=frame.ToBitmap(true);
        var dialog=new Microsoft.Win32.SaveFileDialog{Filter="PNG 图片|*.png",FileName="DeskCam-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".png"};
        if(dialog.ShowDialog(this)!=true)return;
        try{var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(pixels));using var output=File.Create(dialog.FileName);encoder.Save(output);StatusText.Text=$"已保存 {pixels.PixelWidth}×{pixels.PixelHeight}：{dialog.FileName}";statusTick=Stopwatch.GetTimestamp()+Stopwatch.Frequency*5;}
        catch(Exception ex){StatusText.Text="保存失败："+ex.Message;statusTick=Stopwatch.GetTimestamp()+Stopwatch.Frequency*5;}
    }
    private void Autofocus_Click(object sender,RoutedEventArgs e)=>SendCamera("refocus");
    private void FocusLock_Click(object sender,RoutedEventArgs e)=>ToggleFocusLock();
    private void ToggleFocusLock()
    {
        if(!FocusLockButton.IsEnabled)return;
        var locked=camera?.Snapshot.State is {} state&&state.TryGetProperty("focusLocked",out var value)&&value.ValueKind==System.Text.Json.JsonValueKind.True;
        SendCamera(locked?"refocus":"lock");
    }
    private void SendCamera(string kind){try{camera?.Request(kind);}catch(InvalidOperationException ex){StatusText.Text=ex.Message;statusTick=Stopwatch.GetTimestamp()+Stopwatch.Frequency*3;}}
    private void Help_Click(object sender,RoutedEventArgs e)=>ShowHelp();
    private void ShowHelp()=>System.Windows.MessageBox.Show(this,
        "放好手机 → 调倍率与方向 → A 自动对焦 → 看清细字后 L 锁焦 → 截图。\n\n"+
        "Ctrl+C：复制当前原像素画面\nCtrl+S：保存当前裁剪画面\nSpace：冻结 / 恢复预览（冻结后点击画面不会解除）\nL：锁焦 / 恢复自动；A：重新自动对焦\nI：100% 细字检查；H：高清抓拍\nR：旋转；C：框选裁剪；0：重置裁剪\n滚轮缩放，拖动平移；P：显示 / 收起相机栏\nCtrl+1 / Ctrl+2：整张 A4 / 局部推导预设\nF11：纯净模式；Esc / 双击：退出纯净\n\n"+
        "1080p15 适合纸面；1080p30 适合动态书写；4K10 适合高清截图。\nWin+Shift+S 仅保存屏幕像素，完整 4K 用复制或保存。\n锁焦按钮灰色表示 Safari 未开放能力，不等于已锁焦。\n全局复制 Ctrl+Alt+C 若被占用，请用窗口内 Ctrl+C。",
        "DeskCam · 使用与快捷键",MessageBoxButton.OK,MessageBoxImage.Information);
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
        UpdateSidebar();InspectBox.Visibility=inspecting&&!clean?Visibility.Visible:Visibility.Collapsed;
    }
    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) Copy();
        else if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) SaveFrame();
        else if (e.Key == Key.F1) ShowHelp();
        else if (e.Key == Key.D1 && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) cameraPanel?.RestorePreset("整张 A4");
        else if (e.Key == Key.D2 && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) cameraPanel?.RestorePreset("局部推导");
        else if (e.Key == Key.F11 || (e.Key == Key.Escape && clean)) Clean();
        else if (e.Key == Key.C) ToggleCrop();
        else if (e.Key == Key.R) Rotate();
        else if (e.Key is Key.D0 or Key.NumPad0) Reset();
        else if (e.Key == Key.Space) Freeze();
        else if (e.Key == Key.L) ToggleFocusLock();
        else if (e.Key == Key.A && AutofocusButton.IsEnabled) SendCamera("refocus");
        else if (e.Key == Key.I) Inspect_Click(this,new RoutedEventArgs());
        else if (e.Key == Key.H) CaptureHD_Click(this,new RoutedEventArgs());
        else if (e.Key == Key.P) CameraControls_Click(this,new RoutedEventArgs());
        else return;
        e.Handled = true;
    }
    private Point OutputPoint(Point p)
    {
        var scale = Math.Min(Viewport.ActualWidth / displayWidth, Viewport.ActualHeight / displayHeight);
        if (scale <= 0) return new Point();
        return new Point((p.X - (Viewport.ActualWidth - displayWidth * scale) / 2) / scale,
            (p.Y - (Viewport.ActualHeight - displayHeight * scale) / 2) / scale);
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
        Viewport.Focus();
        if (clean && e.ClickCount == 2) { Clean(); return; }
        dragging = true; dragStart = e.GetPosition(Viewport); dragTransform = pipeline.Transform;
        Viewport.CaptureMouse();
        if (cropping) { Selection.Visibility = Visibility.Visible; Selection.Width = Selection.Height = 0; }
    }
    private void Viewport_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if(inspecting){var p=OutputPoint(e.GetPosition(Viewport));inspectPoint=new Point(Math.Clamp(p.X/Math.Max(1,displayWidth),0,1),Math.Clamp(p.Y/Math.Max(1,displayHeight),0,1));}
        if (!dragging || dragTransform is null || pipeline is null) return;
        var now = e.GetPosition(Viewport);
        if((now-dragStart).Length<3)return;
        Resume();
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
