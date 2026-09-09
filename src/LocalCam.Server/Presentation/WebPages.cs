namespace LocalCam.Server.Presentation;

public static class WebPages
{
    public const string Bootstrap = """
        <!doctype html><html lang="zh-CN"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>LocalCam - 安装证书</title><style>body{font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif;margin:2rem;line-height:1.55;color:#172033}a,button{font-size:1rem}code{overflow-wrap:anywhere}</style>
        <h1>LocalCam：首次信任设置</h1><p>这是 Windows 电脑临时提供的本地页面，不会访问互联网。</p>
        <p><a href="/certificate/localcam-ca.cer">下载 LocalCam 本地 CA 证书</a></p>
        <ol><li>下载 LocalCam 证书描述文件。</li><li>打开 iPhone 的“设置 → 通用 → VPN 与设备管理”，完成安装。</li><li><strong>安装后还必须</strong>到“设置 → 通用 → 关于本机 → 证书信任设置”，为 <strong>LocalCam Local CA</strong> 开启完全信任。</li><li>返回 Windows 中的 LocalCam 页面，重新生成并扫描“开始连接”二维码。</li></ol>
        <p><strong>若 Safari 仍显示安全警告，请确认已完全信任当前电脑的证书；旧项目可能有同名但不同的证书。当前证书的 SHA-256 标识可在电脑“连接手机”中核对。电脑 IP 改变后请重启 DeskCam，再用新地址打开。首次正确设置后，日常无需重新安装证书。</strong></p>
        <p>该证书仅用于让此电脑在局域网内以 HTTPS 提供 LocalCam 页面；不要在不受信任的设备上安装。</p></html>
        """;

}
