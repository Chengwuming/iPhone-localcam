using System.IO.MemoryMappedFiles;
using System.ComponentModel;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using LocalCam.Server.Pairing;
using LocalCam.Contracts;
using LocalCam.Updates;

if (args is ["--publish-ipc-test-frame"])
{
    if (!OperatingSystem.IsWindows())
    {
        throw new PlatformNotSupportedException("The IPC diagnostic publisher requires Windows.");
    }
    await IpcDiagnosticPublisher.PublishGreenFrameAsync(TimeSpan.FromSeconds(30));
    return;
}

if (args is ["--send-diagnostic-phone-frame"])
{
    await DiagnosticPhoneSender.SendAsymmetricFramesAsync(TimeSpan.FromSeconds(60));
    return;
}

var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero));
var manager = new PairingManager(clock);
var session = manager.Create();

Assert(manager.IsPending(session.Id, session.Token), "A newly created token must be usable.");
Assert(!manager.IsPending(session.Id, "wrong-token"), "A wrong token must not be accepted.");
Assert(manager.Consume(session.Id, session.Token), "A matching token must be consumed exactly once.");
Assert(!manager.Consume(session.Id, session.Token), "A consumed token must not be reusable.");

var expired = manager.Create();
clock.Advance(TimeSpan.FromMinutes(6));
Assert(!manager.IsPending(expired.Id, expired.Token), "An expired token must not be accepted.");
Assert(manager.RemoveExpired() >= 1, "Expired token cleanup must remove expired sessions.");

FrameIpcContract.ValidateLayout();
Assert(FrameIpcContract.RequiredMappingBytes(1_382_400) > 1_382_400, "The frame mapping must include headers and all slots.");

Assert(GitHubReleaseClient.ParseTag("v0.1.0") == new Version(0, 1, 0, 0), "Stable GitHub tags must parse.");
Assert(GitHubReleaseClient.ParseTag("1.2.3+build.4") == new Version(1, 2, 3, 0), "Build metadata must not affect update comparison.");
Assert(GitHubReleaseClient.Normalize(new Version(0, 1, 0)) == new Version(0, 1, 0, 0), "Version components must normalize before comparison.");
using (var updateHttp = new HttpClient(new StaticReleaseHandler()))
{
    var update = await new GitHubReleaseClient(updateHttp).CheckLatestAsync(
        "example/iPhone-localcam",
        new Version(0, 1, 0));
    Assert(update.IsUpdateAvailable, "A newer GitHub release must be reported as an update.");
    Assert(update.LatestRelease.Version == new Version(0, 2, 0, 0), "The latest release version must be parsed.");
    Assert(update.LatestRelease.InstallerDownload.AbsoluteUri.EndsWith(
        GitHubReleaseClient.InstallerAssetName,
        StringComparison.Ordinal), "The installer asset must be selected by its stable name.");
}

DeskCamTests.Run();
Console.WriteLine("LocalCam smoke tests passed.");

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class ManualTimeProvider(DateTimeOffset initialTime) : TimeProvider
{
    private DateTimeOffset current = initialTime;

    public override DateTimeOffset GetUtcNow() => current;

    public void Advance(TimeSpan duration) => current += duration;
}

sealed class StaticReleaseHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        const string response = """
            {
              "tag_name": "v0.2.0",
              "html_url": "https://github.com/example/iPhone-localcam/releases/tag/v0.2.0",
              "assets": [
                {
                  "name": "LocalCam-Setup-x64.exe",
                  "browser_download_url": "https://github.com/example/iPhone-localcam/releases/download/v0.2.0/LocalCam-Setup-x64.exe"
                }
              ]
            }
            """;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(response)
        });
    }
}

static class IpcDiagnosticPublisher
{
    private const int Width = 640;
    private const int Height = 480;
    private const int PayloadLength = Width * Height * 3 / 2;

    [SupportedOSPlatform("windows")]
    public static async Task PublishGreenFrameAsync(TimeSpan duration)
    {
        FrameIpcContract.ValidateLayout();
        using var mapping = MemoryMappedFile.CreateOrOpen(
            FrameIpcContract.MappingName,
            FrameIpcContract.RequiredMappingBytes(PayloadLength),
            MemoryMappedFileAccess.ReadWrite);
        DiagnosticSharedMemorySecurity.AllowCameraServiceRead(mapping.SafeMemoryMappedFileHandle);
        using var view = mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
        using var frameAvailable = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            FrameIpcContract.FrameAvailableEventName);

        view.Write(0, FrameIpcContract.Magic);
        view.Write(4, FrameIpcContract.MajorVersion);
        view.Write(6, FrameIpcContract.MinorVersion);
        view.Write(8, FrameIpcContract.SlotCount);
        view.Write(12, (uint)PayloadLength);
        view.Write(24, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var nv12 = new byte[PayloadLength];
        Array.Fill(nv12, (byte)144, 0, Width * Height);
        for (var offset = Width * Height; offset < nv12.Length; offset += 2)
        {
            nv12[offset] = 54;
            nv12[offset + 1] = 34;
        }

        var deadline = DateTimeOffset.UtcNow + duration;
        long sequence = 0;
        while (DateTimeOffset.UtcNow < deadline)
        {
            sequence++;
            var slotIndex = (int)((sequence - 1) % FrameIpcContract.SlotCount);
            var slotOffset = 64 + slotIndex * (64 + PayloadLength);
            view.Write(slotOffset, 0L);
            view.Write(slotOffset + 8, DateTime.UtcNow.ToFileTimeUtc());
            view.Write(slotOffset + 16, (uint)Width);
            view.Write(slotOffset + 20, (uint)Height);
            view.Write(slotOffset + 24, (uint)Width);
            view.Write(slotOffset + 28, (uint)FramePixelFormat.Nv12);
            view.Write(slotOffset + 32, (uint)PayloadLength);
            view.WriteArray(slotOffset + 64, nv12, 0, nv12.Length);
            Thread.MemoryBarrier();
            view.Write(slotOffset, sequence);
            view.Write(16, sequence);
            frameAvailable.Set();
            await Task.Delay(100);
        }

        view.Write(16, 0L);
        frameAvailable.Set();
    }
}

[SupportedOSPlatform("windows")]
static partial class DiagnosticSharedMemorySecurity
{
    private const uint DaclSecurityInformation = 0x00000004;
    private const string CameraMappingSddl = "D:(A;;GA;;;SY)(A;;GR;;;LS)(A;;GA;;;IU)";

    public static void AllowCameraServiceRead(SafeMemoryMappedFileHandle handle)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                CameraMappingSddl, 1, out var descriptor, out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            if (!SetKernelObjectSecurity(handle, DaclSecurityInformation, descriptor))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            _ = LocalFree(descriptor);
        }
    }

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSecurityDescriptor,
        uint stringSdRevision,
        out nint securityDescriptor,
        out uint securityDescriptorSize);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetKernelObjectSecurity(
        SafeMemoryMappedFileHandle handle,
        uint securityInformation,
        nint securityDescriptor);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}

static class DiagnosticPhoneSender
{
    public static async Task SendAsymmetricFramesAsync(TimeSpan duration)
    {
        using var http = new HttpClient();
        using var sessionDocument = JsonDocument.Parse(
            await http.GetStringAsync("http://localhost:29100/api/session"));
        var phoneUrl = new Uri(sessionDocument.RootElement.GetProperty("phoneUrl").GetString()
            ?? throw new InvalidOperationException("Session response has no phone URL."));
        var socketUrl = new UriBuilder(phoneUrl)
        {
            Scheme = "wss",
            Path = "/ws/phone"
        }.Uri;

        using var socket = new ClientWebSocket();
        socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        await socket.ConnectAsync(socketUrl, CancellationToken.None);
        var frame = CreateAsymmetricBmp();
        var deadline = DateTimeOffset.UtcNow + duration;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await socket.SendAsync(frame, WebSocketMessageType.Binary, true, CancellationToken.None);
            await Task.Delay(100);
        }
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Diagnostic complete", CancellationToken.None);
    }

    private static byte[] CreateAsymmetricBmp()
    {
        const int pixelOffset = 54;
        const int pixelBytes = 24;
        var bmp = new byte[pixelOffset + pixelBytes];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmp.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), pixelOffset);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), 4);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), 2);
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(28), 24);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(34), pixelBytes);
        for (var row = 0; row < 2; row++)
        {
            var offset = pixelOffset + row * 12;
            bmp[offset + 2] = 255;
            bmp[offset + 5] = 255;
            bmp[offset + 7] = 255;
            bmp[offset + 10] = 255;
        }
        return bmp;
    }
}
