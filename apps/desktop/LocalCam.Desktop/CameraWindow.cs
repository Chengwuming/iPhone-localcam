using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LocalCam.Server.Streaming;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using Orientation = System.Windows.Controls.Orientation;

namespace LocalCam.Desktop;
internal sealed class CameraWindow : Window
{
    private readonly CameraControlStore store;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly ComboBox quality = new(), focus = new();
    private readonly Slider zoom = new(), distance = new();
    private readonly TextBlock actual = new(), status = new(), zoomValue = new(), distanceValue = new();
    private readonly Button qualityApply = new(), zoomApply = new(), focusApply = new(), distanceApply = new(), refocus = new();
    private bool initialized, wasPending;
    public CameraWindow(CameraControlStore store)
    {
        this.store = store; Title = "DeskCam · 相机控制"; Width = 480; Height = 660; MinWidth = 420; MinHeight = 540;
        var panel = new StackPanel { Margin = new Thickness(24) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(new TextBlock { Text = "手机负责拍摄，在电脑调整", FontSize = 22, Margin = new Thickness(0,0,0,14) });
        actual.TextWrapping = TextWrapping.Wrap; panel.Children.Add(actual);
        AddOption(quality, "纸面清晰 · 1080p15", "paper"); AddOption(quality, "标准 · 1080p20", "balanced"); AddOption(quality, "省流 · 720p20", "economy");
        Row(panel, "实时清晰度", quality, qualityApply, () => Send("quality", text: Selected(quality)));
        Row(panel, "相机倍率（采集端）", zoom, zoomApply, () => Send("zoom", zoom.Value)); panel.Children.Add(zoomValue);
        AddOption(focus, "自动对焦", "auto"); AddOption(focus, "手动调焦", "manual"); AddOption(focus, "锁定焦点", "none");
        Row(panel, "对焦方式", focus, focusApply, () => Send("focus", text: Selected(focus)));
        Row(panel, "手动焦点位置", distance, distanceApply, () => Send("distance", distance.Value)); panel.Children.Add(distanceValue);
        refocus.Content = "重新自动对焦"; refocus.Margin = new Thickness(0,12,0,12); refocus.Padding = new Thickness(10);
        refocus.Click += (_,_) => Send("refocus"); panel.Children.Add(refocus);
        zoom.ValueChanged += (_,_) => zoomValue.Text = $"待设倍率 {zoom.Value:F2}×";
        distance.ValueChanged += (_,_) => distanceValue.Text = $"待设焦点 {distance.Value:F3}";
        status.TextWrapping = TextWrapping.Wrap; status.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(140,65,20)); panel.Children.Add(status);
        panel.Children.Add(new TextBlock { Text = "设置会保存到手机。控件取决于 Safari 实际开放的能力；对焦模式不等于已合焦，请观察纸面细字。修改数值后点对应的“应用”。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,16,0,0) });
        timer.Tick += (_,_) => Refresh(); Closed += (_,_) => timer.Stop(); timer.Start(); Refresh();
    }
    private static void AddOption(ComboBox combo, string label, string value) => combo.Items.Add(new ComboBoxItem { Content = label, Tag = value });
    private static string? Selected(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag as string;
    private static void Select(ComboBox combo, string? value) => combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(x => (string)x.Tag == value);
    private static void Row(StackPanel panel, string label, FrameworkElement control, Button apply, Action action)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0,16,0,7) });
        var row = new DockPanel(); apply.Content = "应用"; apply.Padding = new Thickness(12,5,12,5); apply.Margin = new Thickness(12,0,0,0);
        System.Windows.Automation.AutomationProperties.SetName(apply, "应用" + label);
        System.Windows.Automation.AutomationProperties.SetName(control, label);
        DockPanel.SetDock(apply, Dock.Right); row.Children.Add(apply); row.Children.Add(control); panel.Children.Add(row);
        apply.Click += (_,_) => action();
    }
    private void Send(string kind, double? value = null, string? text = null)
    {
        try { store.Request(kind, value, text); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { status.Text = ex.Message; }
        Refresh();
    }
    private static string? Text(JsonElement element, string key) => element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static double? Number(JsonElement element, string key) => element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) ? n : null;
    private static bool Range(JsonElement state, string key, Slider slider, bool initialize, double? current)
    {
        if (!state.TryGetProperty(key, out var range) || range.ValueKind != JsonValueKind.Object || Number(range,"min") is not { } min || Number(range,"max") is not { } max || max <= min) return false;
        slider.Minimum = min; slider.Maximum = max;
        var step = Number(range,"step") ?? 0; slider.TickFrequency = step > 0 ? step : (max-min)/100;
        slider.IsSnapToTickEnabled = true; slider.SmallChange = slider.TickFrequency;
        if (initialize) slider.Value = Math.Clamp(current ?? min,min,max);
        return true;
    }
    private void Refresh()
    {
        var snapshot = store.Snapshot;
        bool synchronize = !initialized || wasPending && !snapshot.Pending;
        wasPending = snapshot.Pending;
        bool fresh = DateTimeOffset.UtcNow - snapshot.UpdatedAt < TimeSpan.FromSeconds(5);
        bool enabled = fresh && !snapshot.Pending;
        foreach (var button in new[] { qualityApply,zoomApply,focusApply,distanceApply,refocus }) button.IsEnabled = enabled;
        status.Text = fresh ? snapshot.Result : "手机未同步；请刷新手机 DeskCam 并保持前台";
        if (snapshot.State is not { } state) return;
        if (state.TryGetProperty("busy",out var busy) && busy.ValueKind == JsonValueKind.True) enabled = false;
        if (!state.TryGetProperty("ready",out var ready) || ready.ValueKind != JsonValueKind.True) enabled = false;
        var settings = state.GetProperty("settings");
        actual.Text = $"实际画面 {Number(settings,"width")}×{Number(settings,"height")} · 倍率 {Number(settings,"zoom")?.ToString("F2") ?? "未返回"}×\n实际对焦模式 {Text(settings,"focusMode") ?? "系统管理"} · 焦点 {Number(settings,"focusDistance")?.ToString("F3") ?? "未返回"}";
        if (synchronize && fresh && Number(settings,"width") is not null)
        { Select(quality,Text(state,"quality")); Select(focus,Text(state,"focus")); }
        bool zoomAvailable = Range(state,"zoomRange",zoom,synchronize,Number(settings,"zoom"));
        bool focusAvailable = Range(state,"focusRange",distance,synchronize,Number(settings,"focusDistance"));
        zoom.IsEnabled = zoomApply.IsEnabled = enabled && zoomAvailable;
        distance.IsEnabled = distanceApply.IsEnabled = enabled && focusAvailable;
        if (!zoomAvailable) zoomValue.Text = "手机未开放相机倍率";
        if (!focusAvailable) distanceValue.Text = "手机未开放手动焦距";
        foreach (ComboBoxItem option in focus.Items)
        {
            string mode = (string)option.Tag;
            option.IsEnabled = mode == "auto" || mode == "manual" && focusAvailable || mode == "none" && state.TryGetProperty("canLock",out var canLock) && canLock.ValueKind == JsonValueKind.True;
        }
        quality.IsEnabled = qualityApply.IsEnabled = focus.IsEnabled = focusApply.IsEnabled = refocus.IsEnabled = enabled;
        if (fresh && Number(settings,"width") is not null) initialized = true;
    }
}
