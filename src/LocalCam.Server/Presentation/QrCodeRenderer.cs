using QRCoder;

namespace LocalCam.Server.Presentation;

public static class QrCodeRenderer
{
    public static string RenderDataUrl(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q);
        // Keep every module crisp after WPF displays the image in the pairing window.
        var png = new PngByteQRCode(data).GetGraphic(10);
        return $"data:image/png;base64,{Convert.ToBase64String(png)}";
    }
}
