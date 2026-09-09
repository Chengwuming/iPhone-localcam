namespace LocalCam.Server.Presentation;

public static class WebPages
{
    public const string Bootstrap = """
        <!doctype html><html lang="zh-CN"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>LocalCam - 安装证书</title><style>body{font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif;margin:2rem;line-height:1.55;color:#172033}a,button{font-size:1rem}code{overflow-wrap:anywhere}</style>
        <h1>LocalCam：首次信任设置</h1><p>这是 Windows 电脑临时提供的本地页面，不会访问互联网。</p>
        <p><a href="/certificate/localcam-ca.cer">下载 LocalCam 本地 CA 证书</a></p>
        <ol><li>下载 LocalCam 证书描述文件。</li><li>打开 iPhone 的“设置 → 通用 → VPN 与设备管理”，完成安装。</li><li><strong>安装后还必须</strong>到“设置 → 通用 → 关于本机 → 证书信任设置”，为 <strong>LocalCam Local CA</strong> 开启完全信任。</li><li>返回 Windows 中的 LocalCam 页面，重新生成并扫描“开始连接”二维码。</li></ol>
        <p><strong>若 Safari 显示“此连接非私人连接”，说明上面的“完全信任”尚未生效。不要绕过警告继续访问，请检查证书信任开关后重新打开连接页面。</strong></p>
        <p>该证书仅用于让此电脑在局域网内以 HTTPS 提供 LocalCam 页面；不要在不受信任的设备上安装。</p></html>
        """;

}
