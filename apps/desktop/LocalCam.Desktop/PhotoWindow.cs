using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LocalCam.Server.Streaming;
using Button = System.Windows.Controls.Button;
using Image = System.Windows.Controls.Image;
using Brushes = System.Windows.Media.Brushes;
using Orientation = System.Windows.Controls.Orientation;
using Panel = System.Windows.Controls.Panel;

namespace LocalCam.Desktop;
internal sealed class PhotoWindow : Window
{
    private readonly CapturedPhoto photo;
    private readonly BitmapSource original;
    private BitmapSource current;
    private readonly Image picture = new() { Stretch = Stretch.Uniform };
    private readonly ScrollViewer scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock info = new() { Margin = new Thickness(12), Foreground = Brushes.White };
    private bool actualPixels;
    private int rotation;
    public PhotoWindow(CapturedPhoto photo)
    {
        this.photo=photo;
        Title = "Capture HD · " + photo.TakenAt.LocalDateTime.ToString("HH:mm:ss"); Width = 1050; Height = 820; MinWidth = 520; MinHeight = 400;
        Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(17, 22, 20));
        using var bytes = new MemoryStream(photo.Jpeg);
        original = BitmapFrame.Create(bytes, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        original.Freeze(); current = original; picture.Source = current;
        var root = new DockPanel(); Content = root;
        var toolbar = new WrapPanel { Margin = new Thickness(10) }; DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);
        AddButton(toolbar, "适合窗口", () => { actualPixels = false; ResizePicture(); });
        AddButton(toolbar, "100% 检查细字", () => { actualPixels = true; ResizePicture(); });
        AddButton(toolbar, "旋转", () =>
        {
            rotation = (rotation + 1) % 4;
            current = rotation == 0 ? original : new TransformedBitmap(original, new RotateTransform(rotation * 90));
            current.Freeze(); picture.Source = current; ResizePicture();
        });
        AddButton(toolbar, "复制 Ctrl+C", CopyPhoto);
        AddButton(toolbar, "保存照片", SavePhoto);
        AddButton(toolbar, "打开保存位置", () => {
            if(photo.LastSavedPath is not {} path){info.Text="尚未保存，请先点保存照片";return;}
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.GetDirectoryName(path)!){UseShellExecute=true}); }
            catch(Exception ex){info.Text="打开失败："+ex.Message;}
        });
        DockPanel.SetDock(info, Dock.Bottom); root.Children.Add(info);
        scroll.Content = picture; root.Children.Add(scroll);
        scroll.SizeChanged += (_, _) => ResizePicture();
        KeyDown += (_, e) => { if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { CopyPhoto(); e.Handled = true; } };
    }
    private static void AddButton(Panel panel, string label, Action action)
    {
        var button = new Button { Content = label, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 0) };
        button.Click += (_, _) => action(); panel.Children.Add(button);
    }
    private void ResizePicture()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        picture.Width = actualPixels ? current.PixelWidth / dpi.DpiScaleX : Math.Max(16, scroll.ActualWidth - 20);
        picture.Height = actualPixels ? current.PixelHeight / dpi.DpiScaleY : Math.Max(16, scroll.ActualHeight - 20);
        info.Text = $"完整照片 {current.PixelWidth}×{current.PixelHeight} · {(actualPixels ? "100% 原像素" : "适合窗口")} · 未套用实时裁剪\n{(photo.LastSavedPath is {} path ? "上次保存："+path : "尚未保存到磁盘；本次运行可从“最近一张”重开")}";
    }
    private void CopyPhoto()
    {
        try { System.Windows.Clipboard.SetImage(current); info.Text = $"已复制 {current.PixelWidth}×{current.PixelHeight} 完整照片"; }
        catch (Exception ex) { info.Text = "复制失败：" + ex.Message; }
    }
    private void SavePhoto()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "PNG 图片|*.png|JPEG 图片|*.jpg", FileName = "DeskCam-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            BitmapEncoder encoder = dialog.FilterIndex == 2 ? new JpegBitmapEncoder { QualityLevel = 98 } : new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(current));
            using var output = File.Create(dialog.FileName); encoder.Save(output);
            photo.LastSavedPath=dialog.FileName;
            info.Text = "已保存：" + dialog.FileName;
        }
        catch (Exception ex) { info.Text = "保存失败：" + ex.Message; }
    }
}
