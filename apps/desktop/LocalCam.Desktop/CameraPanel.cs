using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LocalCam.Server.Streaming;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using UserControl = System.Windows.Controls.UserControl;

namespace LocalCam.Desktop;
internal sealed class CameraPanel : UserControl, IDisposable
{
    private readonly CameraControlStore store;
    private readonly AppSettingsService preferences;
    private readonly Func<ViewTransform> readView;
    private readonly Action<ViewTransform> applyView;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly ComboBox quality = new(), focus = new();
    private readonly Slider zoom = new(), distance = new();
    private readonly TextBlock actual = new(), status = new(), zoomValue = new(), distanceValue = new();
    private readonly Button refocus = new();
    private readonly List<Button> quickZoom = new(), presetButtons = new();
    private bool updating;
    private string? notice;
    private DateTimeOffset noticeUntil;
    private void Notice(string text){notice=text;noticeUntil=DateTimeOffset.UtcNow.AddSeconds(4);status.Text=text;}
    private double? queuedZoom, queuedDistance;
    private (long Id, ViewTransform View)? pendingPreset;
    public CameraPanel(CameraControlStore store, AppSettingsService preferences, Func<ViewTransform> readView, Action<ViewTransform> applyView)
    {
        this.store = store; this.preferences = preferences; this.readView = readView; this.applyView = applyView;
        Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(29,43,34));
        var panel = new StackPanel { Margin = new Thickness(16) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(new TextBlock { Text = "相机控制", FontSize = 20, Margin = new Thickness(0,0,0,12) });
        actual.TextWrapping = TextWrapping.Wrap; panel.Children.Add(actual);
        AddOption(quality,"纸面清晰 · 1080p15","paper"); AddOption(quality,"连续高清截图 · 4K10","ultra");
        AddOption(quality,"流畅 · 1080p30","smooth"); AddOption(quality,"标准 · 1080p20","balanced"); AddOption(quality,"省流 · 720p20","economy");
        Row(panel,"实时清晰度",quality);
        quality.SelectionChanged += (_,_) => { if (!updating) Send("quality",text:Selected(quality)); };
        Row(panel,"相机倍率（拖动即时生效）",zoom); panel.Children.Add(zoomValue);
        var buttons = new WrapPanel(); panel.Children.Add(buttons);
        foreach (double value in new[] {1.0,2,3}) {
            var button = MakeButton($"{value}×",()=> { zoom.Value=value; queuedZoom=value; });
            button.Tag=value; quickZoom.Add(button); buttons.Children.Add(button);
        }
        AddOption(focus,"自动对焦","auto"); AddOption(focus,"手动调焦","manual"); AddOption(focus,"锁定焦点","none");
        Row(panel,"对焦方式",focus); focus.SelectionChanged += (_,_) => { if (!updating) Send("focus",text:Selected(focus)); };
        Row(panel,"手动焦点位置",distance); panel.Children.Add(distanceValue);
        zoom.ValueChanged += (_,_) => { zoomValue.Text=$"倍率 {zoom.Value:F2}×"; if (!updating) queuedZoom=zoom.Value; };
        distance.ValueChanged += (_,_) => { distanceValue.Text=$"焦点 {distance.Value:F3}"; if (!updating) queuedDistance=distance.Value; };
        refocus.Content="重新自动对焦"; refocus.Margin=new Thickness(0,12,0,12); refocus.Click += (_,_)=>Send("refocus"); panel.Children.Add(refocus);
        foreach (var name in new[] {"整张 A4","局部推导"}) {
            panel.Children.Add(new TextBlock {Text=name,Margin=new Thickness(0,12,0,5)});
            var row=new WrapPanel(); panel.Children.Add(row);
            var restore=MakeButton("使用"+name,()=>RestorePreset(name));
            var save=MakeButton("保存到"+name,()=>SavePreset(name));
            row.Children.Add(restore);row.Children.Add(save);presetButtons.Add(restore);presetButtons.Add(save);
        }
        status.TextWrapping=TextWrapping.Wrap;status.Margin=new Thickness(0,14,0,0);panel.Children.Add(status);
        panel.Children.Add(new TextBlock {Text="倍率与焦点会连续应用，只发送最新位置。4K 用于预览和截图，webcam 始终 1080p。预设保存相机参数、方向和裁剪。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,14,0,0),FontSize=12});
        timer.Tick+=(_,_)=>Tick();timer.Start();Tick();
    }
    private static Button MakeButton(string text,Action action) {var button=new Button{Content=text,Padding=new Thickness(9,6,9,6),Margin=new Thickness(0,0,5,4)};button.Click+=(_,_)=>action();return button;}
    private static void Row(StackPanel panel,string label,FrameworkElement control) {
        panel.Children.Add(new TextBlock{Text=label,Margin=new Thickness(0,14,0,7)});
        System.Windows.Automation.AutomationProperties.SetName(control,label);
        if(control is ComboBox combo){combo.Foreground=System.Windows.Media.Brushes.Black;combo.MinHeight=30;}
        panel.Children.Add(control);
    }
    private static void AddOption(ComboBox combo,string label,string value)=>combo.Items.Add(new ComboBoxItem{Content=label,Tag=value});
    private static string? Selected(ComboBox combo)=>(combo.SelectedItem as ComboBoxItem)?.Tag as string;
    private static void Select(ComboBox combo,string? value)=>combo.SelectedItem=combo.Items.Cast<ComboBoxItem>().FirstOrDefault(x=>(string)x.Tag==value);
    private static string? Text(JsonElement e,string key)=>e.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString():null;
    private static double? Number(JsonElement e,string key)=>e.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.Number&&v.TryGetDouble(out var n)?n:null;
    private static bool Flag(JsonElement e,string key)=>e.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.True;
    private static bool Range(JsonElement state,string key,Slider slider,bool sync,double? value) {
        if(!state.TryGetProperty(key,out var range)||range.ValueKind!=JsonValueKind.Object||Number(range,"min") is not {} min||Number(range,"max") is not {} max||max<=min)return false;
        slider.Minimum=min;slider.Maximum=max;var step=Number(range,"step")??0;
        slider.TickFrequency=step>0?step:(max-min)/100;slider.SmallChange=slider.TickFrequency;slider.IsSnapToTickEnabled=true;
        if(sync)slider.Value=Math.Clamp(value??min,min,max);return true;
    }
    private void Send(string kind,double? value=null,string? text=null) {
        try {store.Request(kind,value,text);} catch(InvalidOperationException ex){Notice(ex.Message);}
    }
    private void SavePreset(string name) {
        var snapshot=store.Snapshot;
        if(snapshot.State is not {} state||snapshot.Pending)return;
        var settings=state.GetProperty("settings");
        var presets=new Dictionary<string,PaperPreset>(preferences.Current.PaperPresets??new());
        presets[name]=new(readView(),Text(state,"quality")??"paper",Text(state,"focus")??"auto",Number(settings,"zoom"),Number(settings,"focusDistance"));
        try {preferences.Save(preferences.Current with{PaperPresets=presets});Notice("已保存 "+name);}catch(Exception ex){Notice("保存失败："+ex.Message);}
    }
    internal void RestorePreset(string name) {
        if(preferences.Current.PaperPresets is not {} presets||!presets.TryGetValue(name,out var preset)||preset is null){Notice("先调整画面，再保存到 "+name);return;}
        try {
            queuedZoom=queuedDistance=null;
            var command=store.Request("preset",text:JsonSerializer.Serialize(new{quality=preset.Quality,focus=preset.Focus,zoom=preset.Zoom,distance=preset.Distance}));
            pendingPreset=(command.Id,preset.View);
        }catch(InvalidOperationException ex){Notice(ex.Message);}
    }
    private void Tick() {
        var snapshot=store.Snapshot;bool fresh=DateTimeOffset.UtcNow-snapshot.UpdatedAt<TimeSpan.FromSeconds(3);
        bool sync=!snapshot.Pending;
        if(pendingPreset is {} saved&&!snapshot.Pending){if(snapshot.Result.StartsWith("设置已应用"))applyView(saved.View);pendingPreset=null;}
        if(!fresh){queuedZoom=queuedDistance=null;}
        if(snapshot.State is not {} state){quality.IsEnabled=focus.IsEnabled=zoom.IsEnabled=distance.IsEnabled=refocus.IsEnabled=false;foreach(var button in quickZoom.Concat(presetButtons))button.IsEnabled=false;status.Text="打开手机 DeskCam 后即可调整";return;}
        bool available=fresh&&Flag(state,"ready")&&!Flag(state,"acquiring");
        if(available&&!snapshot.Pending){
            if(queuedZoom is {} z){queuedZoom=null;Send("zoom",z);snapshot=store.Snapshot;sync=false;}
            else if(queuedDistance is {} d){queuedDistance=null;Send("distance",d);snapshot=store.Snapshot;sync=false;}
        }
        var settings=state.GetProperty("settings");updating=true;
        try {
            if(sync&&available){Select(quality,Text(state,"quality"));Select(focus,Text(state,"focus"));}
            bool zr=Range(state,"zoomRange",zoom,sync&&queuedZoom is null&&!zoom.IsMouseCaptureWithin,Number(settings,"zoom"));
            bool dr=Range(state,"focusRange",distance,sync&&queuedDistance is null&&!distance.IsMouseCaptureWithin,Number(settings,"focusDistance"));
            zoom.IsEnabled=available&&zr;distance.IsEnabled=available&&dr;
            foreach(var button in quickZoom)button.IsEnabled=available&&zr&&(double)button.Tag>=zoom.Minimum&&(double)button.Tag<=zoom.Maximum;
            if(!zr)zoomValue.Text="手机未开放相机倍率";if(!dr)distanceValue.Text="手机未开放手动焦距";
            quality.IsEnabled=focus.IsEnabled=refocus.IsEnabled=available&&!snapshot.Pending&&queuedZoom is null&&queuedDistance is null;
            foreach(var button in presetButtons)button.IsEnabled=quality.IsEnabled;
            foreach(ComboBoxItem option in focus.Items){var mode=(string)option.Tag;option.IsEnabled=mode=="auto"||mode=="manual"&&dr||mode=="none"&&Flag(state,"canLock");}
            actual.Text=$"实际 {Number(settings,"width")}×{Number(settings,"height")} · {Number(settings,"frameRate")?.ToString("F0")??"—"} fps · {Number(settings,"zoom")?.ToString("F2")??"—"}×\n焦点 {Number(settings,"focusDistance")?.ToString("F3")??"系统管理"} · {Text(settings,"focusMode")??"系统模式"}";
            status.Text=fresh?(DateTimeOffset.UtcNow<noticeUntil?notice:snapshot.Result):"手机离线；请保持 DeskCam 在前台";

        }finally{updating=false;}
    }
    public void Dispose()=>timer.Stop();
}
