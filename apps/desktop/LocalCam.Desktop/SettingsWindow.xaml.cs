using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using LocalCam.Updates;

namespace LocalCam.Desktop;

public partial class SettingsWindow : Window
{
    private static readonly HttpClient UpdateHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };
    private readonly AppSettingsService settingsService;

    internal SettingsWindow(AppSettingsService settingsService)
    {
        this.settingsService = settingsService;
        InitializeComponent();
        var selectedTag = settingsService.Current.CloseBehavior.ToString();
        CloseBehaviorSelector.SelectedItem = CloseBehaviorSelector.Items
            .OfType<ComboBoxItem>()
            .First(item => string.Equals(item.Tag?.ToString(), selectedTag, StringComparison.Ordinal));
        StartWithWindowsCheckBox.IsChecked = settingsService.Current.StartWithWindows;
        CurrentVersionText.Text = $"当前版本：{GitHubReleaseClient.Normalize(ApplicationReleaseInfo.CurrentVersion).ToString(3)}";
        UpdateStatusText.Text = "仅在点击检查时连接 GitHub。";
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "正在检查 GitHub Release…";
        ErrorText.Text = string.Empty;
        try
        {
            var repository = ApplicationReleaseInfo.GitHubRepository;
            if (string.IsNullOrWhiteSpace(repository))
            {
                throw new InvalidOperationException("当前构建没有配置 GitHub 仓库地址。");
            }

            var client = new GitHubReleaseClient(UpdateHttpClient);
            var result = await client.CheckLatestAsync(repository, ApplicationReleaseInfo.CurrentVersion);
            if (!result.IsUpdateAvailable)
            {
                UpdateStatusText.Text = $"已是最新版本（{result.LatestRelease.TagName}）。";
                return;
            }

            UpdateStatusText.Text = $"发现新版本 {result.LatestRelease.TagName}。";
            var choice = System.Windows.MessageBox.Show(
                this,
                $"发现 LocalCam {result.LatestRelease.TagName}。\n\n是否通过 GitHub 下载最新安装包？",
                "LocalCam 更新",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (choice == MessageBoxResult.Yes)
            {
                Process.Start(new ProcessStartInfo(result.LatestRelease.InstallerDownload.AbsoluteUri)
                {
                    UseShellExecute = true
                });
                UpdateStatusText.Text = "已在浏览器中打开 GitHub 安装包下载。";
            }
        }
        catch (HttpRequestException exception)
        {
            UpdateStatusText.Text = "检查失败。";
            ErrorText.Text = $"无法连接 GitHub：{exception.Message}";
        }
        catch (TaskCanceledException)
        {
            UpdateStatusText.Text = "检查超时。";
            ErrorText.Text = "GitHub 响应超时，请稍后重试。";
        }
        catch (Exception exception)
        {
            UpdateStatusText.Text = "检查失败。";
            ErrorText.Text = exception.Message;
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var selected = (CloseBehaviorSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            if (!Enum.TryParse<CloseBehavior>(selected, out var closeBehavior))
            {
                closeBehavior = CloseBehavior.Exit;
            }

            settingsService.Save(new LocalCamSettings(
                closeBehavior,
                StartWithWindowsCheckBox.IsChecked == true,
                settingsService.Current.WindowPlacement));
            DialogResult = true;
        }
        catch (Exception exception)
        {
            ErrorText.Text = $"保存设置失败：{exception.Message}";
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
