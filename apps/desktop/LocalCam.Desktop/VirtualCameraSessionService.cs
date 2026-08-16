using System.Diagnostics;
using System.IO;

namespace LocalCam.Desktop;

internal sealed class VirtualCameraSessionService : IDisposable
{
    private Process? sessionProcess;

    public string StatusText { get; private set; } = "虚拟摄像头尚未启动";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var registrar = FindRegistrar();
        if (registrar is null)
        {
            StatusText = "未找到虚拟摄像头组件";
            return;
        }

        await RemoveLegacySystemCameraAsync(registrar, cancellationToken);
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(registrar, "--run-session")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            },
            EnableRaisingEvents = true
        };
        if (!process.Start())
        {
            process.Dispose();
            StatusText = "虚拟摄像头启动失败";
            return;
        }

        using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        string? line;
        try
        {
            line = await process.StandardOutput.ReadLineAsync(startupTimeout.Token);
        }
        catch
        {
            TryTerminate(process);
            process.Dispose();
            throw;
        }

        if (!string.Equals(line, "READY", StringComparison.Ordinal))
        {
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            TryTerminate(process);
            process.Dispose();
            StatusText = string.IsNullOrWhiteSpace(error)
                ? "虚拟摄像头启动失败"
                : $"虚拟摄像头启动失败：{error.Trim()}";
            return;
        }

        sessionProcess = process;
        StatusText = "LocalCam Camera 已随软件启动";
    }

    private static async Task RemoveLegacySystemCameraAsync(string registrar, CancellationToken cancellationToken)
    {
        using var cleanup = Process.Start(new ProcessStartInfo(registrar, "--remove-legacy-system")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        if (cleanup is not null)
        {
            await cleanup.WaitForExitAsync(cancellationToken);
        }
    }

    private static string? FindRegistrar()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "VirtualCamera", "LocalCamVirtualCameraRegistrar.exe");
        if (File.Exists(bundled))
        {
            return bundled;
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var developmentBuild = Path.Combine(
                directory.FullName,
                "artifacts",
                "virtual-camera",
                "live",
                "LocalCamVirtualCameraRegistrar.exe");
            if (File.Exists(developmentBuild))
            {
                return developmentBuild;
            }
        }

        return null;
    }

    public void Dispose()
    {
        var process = Interlocked.Exchange(ref sessionProcess, null);
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.WriteLine("shutdown");
                process.StandardInput.Close();
                if (!process.WaitForExit(5000))
                {
                    TryTerminate(process);
                }
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                process.WaitForExit(3000);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
