using System.Net;
using LocalCam.Server.Network;
using LocalCam.Server.Pairing;
using LocalCam.Server.Presentation;
using LocalCam.Server.Security;
using LocalCam.Server.Streaming;

namespace LocalCam.Server;

public sealed class LocalCamServerInstance(WebApplication application) : IAsyncDisposable
{
    public Task WaitForShutdownAsync(CancellationToken cancellationToken = default) =>
        application.WaitForShutdownAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default) =>
        application.StopAsync(cancellationToken);

    public ValueTask DisposeAsync() => application.DisposeAsync();
}

public static class LocalCamServerHost
{
    public const int BootstrapPort = 29100;
    public const int HttpsPort = 29101;

    public static async Task<LocalCamServerInstance> StartAsync(
        string[]? args = null,
        CancellationToken cancellationToken = default)
    {
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LocalCam");
        var addresses = LocalNetworkAddressProvider.GetUsableIPv4Addresses()
            .Append(LocalNetworkAddressProvider.WindowsMobileHotspotDefaultAddress)
            .Distinct()
            .ToArray();
        var certificateAuthority = new LocalCertificateAuthority(Path.Combine(dataDirectory, "certificates"));
        var certificate = certificateAuthority.CreateServerCertificate(addresses);

        var builder = WebApplication.CreateBuilder(args ?? []);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(BootstrapPort);
            options.ListenAnyIP(HttpsPort, listen => listen.UseHttps(certificate));
        });

        var app = builder.Build();
        var pairing = new PairingManager(TimeProvider.System);
        var relay = new FrameRelay();
        app.UseWebSockets();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            await next();
        });

        app.MapGet("/", (HttpContext context) =>
        {
            if (!context.Request.IsHttps)
            {
                return Results.Content(WebPages.Bootstrap, "text/html; charset=utf-8");
            }

            return Results.NotFound();
        });

        app.MapGet("/monitor", (HttpContext context) =>
            IsLocal(context) ? Results.Content(WebPages.Monitor, "text/html; charset=utf-8") : Results.NotFound());

        app.MapGet("/certificate/localcam-ca.cer", () =>
            Results.File(certificateAuthority.PublicCertificatePath, "application/x-x509-ca-cert", "LocalCam-Local-CA.cer"));

        app.MapGet("/api/session", (HttpContext context) =>
        {
            if (!IsLocal(context))
            {
                return Results.NotFound();
            }

            var requestedAddress = context.Request.Query["address"].ToString();
            var routes = LocalNetworkAddressProvider.GetUsableRoutes();
            var selectedRoute = routes.FirstOrDefault(route => route.Address.ToString() == requestedAddress)
                ?? LocalNetworkAddressProvider.GetPreferredRoute();
            if (selectedRoute is null)
            {
                return Results.BadRequest("未检测到可用于本地连接的 IPv4 网络。请先连接同一 Wi-Fi、手机热点、Windows 移动热点或 USB 网络。\n");
            }

            var session = pairing.Create();
            var baseUrl = $"https://{selectedRoute.Address}:{HttpsPort}";
            var bootstrapUrl = $"http://{selectedRoute.Address}:{BootstrapPort}/";
            var phoneUrl = $"{baseUrl}/phone?session={Uri.EscapeDataString(session.Id)}&token={Uri.EscapeDataString(session.Token)}";
            return Results.Json(new
            {
                bootstrapUrl,
                bootstrapQr = QrCodeRenderer.RenderDataUrl(bootstrapUrl),
                phoneUrl,
                phoneQr = QrCodeRenderer.RenderDataUrl(phoneUrl),
                connectionName = selectedRoute.Name,
                connectionType = LocalNetworkAddressProvider.GetConnectionType(selectedRoute),
                isWindowsMobileHotspot = selectedRoute.IsWindowsMobileHotspot,
                isPhoneWifiHotspot = selectedRoute.IsPhoneWifiHotspot,
                session.ExpiresAt
            });
        });

        app.MapGet("/api/networks", (HttpContext context) =>
        {
            if (!IsLocal(context))
            {
                return Results.NotFound();
            }

            var routes = LocalNetworkAddressProvider.GetUsableRoutes();
            var preferredAddress = LocalNetworkAddressProvider.GetPreferredRoute()?.Address;
            return Results.Json(routes.Select(route => new
            {
                address = route.Address.ToString(),
                name = route.Name,
                connectionType = LocalNetworkAddressProvider.GetConnectionType(route),
                isWindowsMobileHotspot = route.IsWindowsMobileHotspot,
                isPhoneWifiHotspot = route.IsPhoneWifiHotspot,
                isRecommended = route.Address.Equals(preferredAddress)
            }));
        });

        app.MapGet("/phone", (HttpContext context, string? session, string? token) =>
        {
            if (!context.Request.IsHttps || !pairing.IsPending(session ?? string.Empty, token ?? string.Empty))
            {
                return Results.BadRequest("配对链接不存在、已过期或已被使用。请回到 Windows 页面重新生成二维码。");
            }

            return Results.Content(WebPages.Phone, "text/html; charset=utf-8");
        });

        app.Map("/ws/phone", async context =>
        {
            var session = context.Request.Query["session"].ToString();
            var token = context.Request.Query["token"].ToString();
            if (!context.Request.IsHttps || !context.WebSockets.IsWebSocketRequest || !pairing.Consume(session, token))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            relay.SetActivePhone(socket);
            try
            {
                await relay.ReceivePhoneFramesAsync(socket, context.RequestAborted);
            }
            finally
            {
                relay.RemoveActivePhone(socket);
            }
        });

        app.Map("/ws/monitor", async context =>
        {
            if (!IsLocal(context) || !context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await relay.AddMonitorAsync(socket, context.RequestAborted);
        });

        app.MapGet("/api/status", (HttpContext context) =>
        {
            if (!IsLocal(context))
            {
                return Results.NotFound();
            }

            return Results.Json(new
            {
                relay.FramesReceived,
                relay.LastFrameAt,
                relay.MonitorCount,
                phoneConnected = relay.IsPhoneConnected,
                preferredAddress = LocalNetworkAddressProvider.GetPreferredRoute()?.Address.ToString(),
                ports = new { BootstrapPort, HttpsPort }
            });
        });

        await app.StartAsync(cancellationToken);
        return new LocalCamServerInstance(app);
    }

    private static bool IsLocal(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        return remote is not null && IPAddress.IsLoopback(remote);
    }
}
