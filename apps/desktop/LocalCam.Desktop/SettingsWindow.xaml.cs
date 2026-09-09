using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
namespace LocalCam.Desktop;
public partial class SettingsWindow : Window
{
    private readonly AppSettingsService settingsService;
    internal SettingsWindow(AppSettingsService settingsService)
    {
        this.settingsService = settingsService; InitializeComponent();
        CloseBehaviorSelector.SelectedItem = CloseBehaviorSelector.Items.OfType<ComboBoxItem>()
            .First(item => item.Tag?.ToString() == settingsService.Current.CloseBehavior.ToString());
        StartWithWindowsCheckBox.IsChecked = settingsService.Current.StartWithWindows;
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Enum.TryParse<CloseBehavior>((CloseBehaviorSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var behavior);
            settingsService.Save(settingsService.Current with { CloseBehavior = behavior, StartWithWindows = StartWithWindowsCheckBox.IsChecked == true });
            DialogResult = true;
        }
        catch (Exception ex) { ErrorText.Text = "保存失败：" + ex.Message; }
    }
    private async void Unpair_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var client = new HttpClient();
            using var response = await client.PostAsync("http://127.0.0.1:29100/api/unpair", null);
            response.EnsureSuccessStatusCode();
            ErrorText.Text = "已取消配对。下次在“连接手机”中重新扫码。";
        }
        catch (Exception ex) { ErrorText.Text = ex.Message; }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
