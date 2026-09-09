using System.Net;
using LocalCam.Server.Network;
using LocalCam.Server.Pairing;
using LocalCam.Server.Presentation;
using LocalCam.Server.Security;
using LocalCam.Server.Streaming;

namespace LocalCam.Server;

public sealed class LocalCamServerInstance(WebApplication application, FrameRelay relay) : IAsyncDisposable
{
    public FrameRelay Relay { get; } = relay;
    public PhotoStore Photos { get; } = new();
    public CameraControlStore Camera { get; } = new();
    public Func<byte[]?>? Snapshot { get; set; }
    public Func<object>? VideoStatus { get; set; }
    public Task WaitForShutdownAsync(CancellationToken ct = default) => application.WaitForShutdownAsync(ct);
    public Task StopAsync(CancellationToken ct = default) => application.StopAsync(ct);
    public ValueTask DisposeAsync() => application.DisposeAsync();
}
public static class LocalCamServerHost
{
    public const int BootstrapPort = 29100;
    public const int HttpsPort = 29101;
    public static async Task<LocalCamServerInstance> StartAsync(string[]? args = null, CancellationToken cancellationToken = default)
    {
        // Integration tests use a separate credential/certificate store, preserving the paired phone.
        var dataDirectory = Environment.GetEnvironmentVariable("DESKCAM_DATA_DIR") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalCam");
        var addresses = LocalNetworkAddressProvider.GetUsableIPv4Addresses()
            .Append(LocalNetworkAddressProvider.WindowsMobileHotspotDefaultAddress).Distinct().ToArray();
        var ca = new LocalCertificateAuthority(Path.Combine(dataDirectory, "certificates"));
        var certificate = ca.CreateServerCertificate(addresses);
        var builder = WebApplication.CreateBuilder(args ?? []);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(BootstrapPort);
            options.ListenAnyIP(HttpsPort, listen => listen.UseHttps(certificate));
        });
        var app = builder.Build();
        var pairing = new PairingManager(TimeProvider.System);
        var devices = new DeviceStore(dataDirectory);
        var relay = new FrameRelay();
        var instance = new LocalCamServerInstance(app, relay);
        var addressFile = Path.Combine(dataDirectory, "deskcam-address.txt");
        string? preferred = File.Exists(addressFile) ? File.ReadAllText(addressFile).Trim() : null;
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(10), KeepAliveTimeout = TimeSpan.FromSeconds(15) });
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            await next();
        });
        string? Preferred() => LocalNetworkAddressProvider.GetUsableRoutes().FirstOrDefault(r => r.Address.ToString() == preferred)?.Address.ToString()
            ?? LocalNetworkAddressProvider.GetPreferredRoute()?.Address.ToString();
        app.MapGet("/", (HttpContext c) => c.Request.IsHttps ? Results.Redirect("/phone") : Results.Content(WebPages.Bootstrap, "text/html; charset=utf-8"));
        app.MapGet("/certificate/localcam-ca.cer", () => Results.File(ca.PublicCertificatePath, "application/x-x509-ca-cert", "LocalCam-Local-CA.cer"));
        app.MapGet("/api/networks", (HttpContext c) => !IsLocal(c) ? Results.NotFound() :
            Results.Json(LocalNetworkAddressProvider.GetUsableRoutes().Select(r => new
            {
                address = r.Address.ToString(), name = r.Name, connectionType = LocalNetworkAddressProvider.GetConnectionType(r),
                isWindowsMobileHotspot = r.IsWindowsMobileHotspot, isPhoneWifiHotspot = r.IsPhoneWifiHotspot,
                isRecommended = r.Address.ToString() == Preferred()
            })));
        app.MapGet("/api/session", (HttpContext c) =>
        {
            if (!IsLocal(c)) return Results.NotFound();
            var requested = c.Request.Query["address"].ToString();
            var route = LocalNetworkAddressProvider.GetUsableRoutes().FirstOrDefault(r => r.Address.ToString() == requested)
                ?? LocalNetworkAddressProvider.GetUsableRoutes().FirstOrDefault(r => r.Address.ToString() == Preferred());
            if (route is null) return Results.BadRequest("未找到手机可达的 IPv4 网卡。");
            preferred = route.Address.ToString();
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllText(addressFile, preferred);
            var session = pairing.Create();
            var phoneUrl = $"https://{route.Address}:{HttpsPort}/phone#session={session.Id}&token={session.Token}";
            var bootstrapUrl = $"http://{route.Address}:{BootstrapPort}/";
            return Results.Json(new
            {
                phoneUrl, phoneQr = QrCodeRenderer.RenderDataUrl(phoneUrl),
                bootstrapUrl, bootstrapQr = QrCodeRenderer.RenderDataUrl(bootstrapUrl),
                connectionName = route.Name, connectionType = LocalNetworkAddressProvider.GetConnectionType(route), session.ExpiresAt
            });
        });
        app.MapGet("/phone", (HttpContext c) => c.Request.IsHttps ? Asset("phone.html", "text/html; charset=utf-8") : Results.NotFound());
        app.MapGet("/phone.js", (HttpContext c) => c.Request.IsHttps ? Asset("phone.js", "text/javascript; charset=utf-8") : Results.NotFound());
        app.MapGet("/camera-frame.mjs", (HttpContext c) => c.Request.IsHttps ? Asset("camera-frame.mjs", "text/javascript; charset=utf-8") : Results.NotFound());
        app.MapGet("/camera-controls.mjs", (HttpContext c) => c.Request.IsHttps ? Asset("camera-controls.mjs", "text/javascript; charset=utf-8") : Results.NotFound());
        app.MapGet("/manifest.json", (HttpContext c) => c.Request.IsHttps ? Asset("manifest.json", "application/manifest+json") : Results.NotFound());
        app.MapGet("/icon.svg", () => Asset("icon.svg", "image/svg+xml"));
        app.MapPost("/api/pair", async (HttpContext c) =>
        {
            if (!c.Request.IsHttps || !SameOrigin(c)) return Results.StatusCode(403);
            var request = await c.Request.ReadFromJsonAsync<PairRequest>(c.RequestAborted);
            if (request is null || !pairing.Consume(request.Session, request.Token)) return Results.StatusCode(403);
            var credential = devices.Pair();
            c.Response.Cookies.Append("deskcam", credential, new CookieOptions
            { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, MaxAge = TimeSpan.FromDays(365), Path = "/" });
            return Results.Json(new { paired = true });
        });
        app.MapGet("/api/paired", (HttpContext c) => c.Request.IsHttps && devices.Validate(c.Request.Cookies["deskcam"]) ?
            Results.Json(new { paired = true }) : Results.StatusCode(403));
        app.MapPost("/api/unpair", (HttpContext c) =>
        {
            if (!IsLocal(c) || c.Request.Headers.ContainsKey("Origin")) return Results.NotFound();
            devices.Revoke();
            relay.Disconnect();
            return Results.Ok();
        });
        app.Map("/ws/phone", async c =>
        {
            if (!c.Request.IsHttps || !SameOrigin(c) || !c.WebSockets.IsWebSocketRequest || !devices.Validate(c.Request.Cookies["deskcam"]))
            { c.Response.StatusCode = 403; return; }
            using var socket = await c.WebSockets.AcceptWebSocketAsync();
            await relay.ReceivePhoneFramesAsync(socket, c.RequestAborted);
        });
        app.MapGet("/api/status", (HttpContext c) => !IsLocal(c) ? Results.NotFound() : Results.Json(new
        {
            phoneConnected = relay.IsPhoneConnected, relay.FramesReceived, relay.BytesReceived, relay.LastFrameAt,
            error = relay.Error, preferredAddress = Preferred(), video = instance.VideoStatus?.Invoke(), camera = instance.Camera.Snapshot
        }));
        app.MapGet("/api/frame.jpg", (HttpContext c) =>
        {
            if (!IsLocal(c)) return Results.NotFound();
            var bytes = instance.Snapshot?.Invoke();
            return bytes is null ? Results.StatusCode(503) : Results.File(bytes, "image/jpeg");
        });
        int photoUpload = 0;
        app.MapPost("/api/camera/sync", async (HttpContext c) =>
        {
            if (!c.Request.IsHttps || !SameOrigin(c) || !devices.Validate(c.Request.Cookies["deskcam"])) return Results.StatusCode(403);
            if (c.Request.ContentLength is not > 0 or > 16384) return Results.BadRequest();
            var request = await c.Request.ReadFromJsonAsync<CameraSync>(c.RequestAborted);
            if (request is null || request.State.ValueKind != System.Text.Json.JsonValueKind.Object) return Results.BadRequest();
            return Results.Json(new { command = instance.Camera.Exchange(request.State, request.Ack, request.Message) });
        });
        app.MapPost("/api/photo/request", (HttpContext c) =>
        {
            if (!IsLocal(c) || c.Request.Headers.ContainsKey("Origin")) return Results.NotFound();
            try { return Results.Json(instance.Camera.Request("photo")); }
            catch (InvalidOperationException ex) { return Results.Conflict(ex.Message); }
        });
        app.MapGet("/api/photo/request", (HttpContext c) =>
            !c.Request.IsHttps || !devices.Validate(c.Request.Cookies["deskcam"]) ? Results.StatusCode(403) :
            Results.Json(new { id = instance.Photos.TakeRequest() }));
        app.MapPost("/api/photo", async (HttpContext c) =>
        {
            if (!c.Request.IsHttps || !SameOrigin(c) || !devices.Validate(c.Request.Cookies["deskcam"])) return Results.StatusCode(403);
            if (Interlocked.CompareExchange(ref photoUpload, 1, 0) != 0) return Results.StatusCode(429);
            try
            {
                if (c.Request.ContentLength > PhotoStore.MaximumBytes) return Results.StatusCode(413);
                using var data = new MemoryStream(); var buffer = new byte[65536]; int read;
                while ((read = await c.Request.Body.ReadAsync(buffer, c.RequestAborted)) > 0)
                {
                    if (data.Length + read > PhotoStore.MaximumBytes) return Results.StatusCode(413);
                    data.Write(buffer, 0, read);
                }
                var source = c.Request.Query["source"].ToString() switch { "system-camera" => "system-camera", "hd-frame" => "hd-frame", _ => "camera-photo" };
                var photo = instance.Photos.Accept(data.ToArray(), source);
                return Results.Json(new { photo.Sequence, photo.Width, photo.Height });
            }
            catch (InvalidDataException ex) { return Results.BadRequest(ex.Message); }
            finally { Interlocked.Exchange(ref photoUpload, 0); }
        });
        app.MapGet("/api/photo.jpg", (HttpContext c) => !IsLocal(c) ? Results.NotFound() :
            instance.Photos.Latest is { } photo ? Results.File(photo.Jpeg, "image/jpeg") : Results.NotFound());
        await app.StartAsync(cancellationToken);
        return instance;
    }
    private static IResult Asset(string name, string type)
    {
        var stream = typeof(LocalCamServerHost).Assembly.GetManifestResourceStream("LocalCam.Server.Web." + name);
        return stream is null ? Results.NotFound() : Results.Stream(stream, type);
    }
    private static bool IsLocal(HttpContext c) => c.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip);
    private static bool SameOrigin(HttpContext c) => Uri.TryCreate(c.Request.Headers.Origin, UriKind.Absolute, out var origin)
        && origin.Scheme == "https" && origin.Authority.Equals(c.Request.Host.Value, StringComparison.OrdinalIgnoreCase);
    private sealed record PairRequest(string Session, string Token);
    private sealed record CameraSync(System.Text.Json.JsonElement State, long Ack, string? Message);
}
