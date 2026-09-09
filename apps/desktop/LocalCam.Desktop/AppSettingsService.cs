using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace LocalCam.Desktop;

internal enum CloseBehavior
{
    Exit,
    MinimizeToTray
}

internal sealed record WindowPlacementSettings(
    double Left,
    double Top,
    double Width,
    double Height,
    bool WasMaximized);

internal sealed record LocalCamSettings(
    CloseBehavior CloseBehavior,
    bool StartWithWindows,
    WindowPlacementSettings? WindowPlacement = null,
    ViewTransform? View = null,
    bool AlwaysOnTop = false)
{
    public static LocalCamSettings Default { get; } = new(CloseBehavior.MinimizeToTray, false, null);
}

internal sealed class AppSettingsService
{
    private const string StartupValueName = "LocalCam";
    private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string settingsPath = Path.Combine(Environment.GetEnvironmentVariable("DESKCAM_DATA_DIR") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalCam"), "settings.json");

    public LocalCamSettings Current { get; private set; } = LocalCamSettings.Default;

    public void Load()
    {
        try
        {
            if (!File.Exists(settingsPath))
            {
                return;
            }

            var settings = JsonSerializer.Deserialize<LocalCamSettings>(File.ReadAllText(settingsPath));
            if (settings is not null && Enum.IsDefined(settings.CloseBehavior))
            {
                Current = settings;
            }
        }
        catch (JsonException)
        {
            // Keep safe defaults if an older or damaged settings file cannot be read.
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Save(LocalCamSettings settings)
    {
        SaveCore(settings);
        ApplyStartupRegistration(settings.StartWithWindows);
    }

    public void SaveWindowPlacement(WindowPlacementSettings placement)
    {
        try
        {
            SaveCore(Current with { WindowPlacement = placement });
        }
        catch (IOException)
        {
            // Window shutdown and tray transitions must never fail because settings could not be written.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void SaveCore(LocalCamSettings settings)
    {
        var directory = Path.GetDirectoryName(settingsPath)
            ?? throw new InvalidOperationException("无法确定 LocalCam 设置目录。");
        Directory.CreateDirectory(directory);
        var temporaryPath = settingsPath + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
            File.Move(temporaryPath, settingsPath, true);
            Current = settings;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void ApplyStartupRegistration(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(StartupRegistryPath, true)
            ?? throw new InvalidOperationException("无法打开当前用户的开机启动设置。");
        if (enabled)
        {
            var executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("无法获取 LocalCam 程序路径。");
            key.SetValue(StartupValueName, $"\"{executable}\" --minimized", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(StartupValueName, false);
        }
    }
}
