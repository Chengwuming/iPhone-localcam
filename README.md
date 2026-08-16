# LocalCam

LocalCam 可以把 iPhone Safari 的摄像头画面通过局域网发送到 Windows 11，并在电脑上提供原生预览和名为 `LocalCam Camera` 的虚拟摄像头。它适合临时没有合适电脑摄像头，或希望在会议软件、OBS 等程序中使用 iPhone 画面的场景。

视频传输、配对和证书都在 iPhone 与 Windows 电脑之间完成，不需要账号、云端视频服务、域名、STUN 或 TURN。只有用户在设置中主动点击“检查更新”时，软件才会访问 GitHub Release。

## 主要功能

- iPhone Safari 扫码连接，同一 WLAN、Windows 移动热点、iPhone 个人热点或可用的 iPhone USB 网络均可使用。
- 一次性配对链接：256-bit 随机令牌、5 分钟有效、连接后立即失效。
- Windows 原生预览，无需保持浏览器窗口打开。
- 可切换前后摄像头、20/30 FPS、完整显示/铺满窗口、实时缩放和水平镜像。
- `LocalCam Camera` 虚拟摄像头，可供 Windows“相机”、会议软件或 OBS 选择。
- iPhone 断开或画面超时后自动切换为静态黑色画面，不保留历史帧。
- 托盘运行、关闭到托盘、开机启动和窗口位置记忆。
- 设置页手动检查 GitHub 更新；发现新版后在浏览器中打开最新安装包下载。

## 安装方法

### 普通用户

1. 打开本仓库的 **Releases** 页面。
2. 下载最新版本中的 `LocalCam-Setup-x64.exe`。
3. 运行安装程序。注册 Windows 虚拟摄像头需要管理员权限。
4. 从开始菜单启动 LocalCam。

安装包已经包含 .NET 运行时，普通用户不需要另外安装 .NET SDK。当前安装包尚未进行商业代码签名，Windows SmartScreen 可能显示未知发布者提示。

支持范围：Windows 11 x64（Build 22000 或更高版本）和带 Safari 的 iPhone。

升级时直接运行新版本安装包即可。卸载会移除程序文件和虚拟摄像头注册，但会保留 `%LOCALAPPDATA%\LocalCam` 中的本地设置和证书，方便以后继续使用。

## 使用方法

1. 确保 iPhone 与 Windows 电脑处于能够互相访问的本地网络。
2. 启动 LocalCam，点击“连接 iPhone”。
3. 首次使用时，用 iPhone 扫描“安装证书”二维码：
   - 按 iOS 提示安装描述文件；
   - 再前往“设置 → 通用 → 关于本机 → 证书信任设置”；
   - 为 `LocalCam Local CA` 开启完全信任。
4. 回到 LocalCam，重新生成并扫描连接二维码。
5. 在 Safari 中允许摄像头访问。
6. 在需要摄像头的软件中选择 `LocalCam Camera`。

配对二维码默认 5 分钟有效，并且只能成功使用一次。如果二维码过期、切换了网络或 Safari 仍提示证书不受信任，请在 LocalCam 中重新生成二维码并检查证书信任开关。

## 输入输出示例

| 项目 | 示例 |
| --- | --- |
| 输入设备 | iPhone 后置摄像头 |
| 输入设置 | Safari、720p 采集请求、20 FPS、JPEG/WSS 局域网传输 |
| Windows 原生预览 | 实时显示 iPhone 画面和接收帧率 |
| 虚拟摄像头输出 | `LocalCam Camera`，640×480 NV12 |
| 控制输入 | 切换前后镜头、20/30 FPS、缩放、镜像、适应/填充 |
| 断开输出 | 3 秒没有新帧后显示静态黑色画面 |

例如，在 LocalCam 中连接 iPhone 后打开 OBS，新增“视频采集设备”，设备选择 `LocalCam Camera`，OBS 即会收到 LocalCam 转换后的虚拟摄像头画面。

## 手动检查更新

打开“设置 → 软件更新 → 检查更新”。LocalCam 会查询 GitHub 的最新正式 Release：

- 已是最新版本时显示当前状态；
- 有新版本时询问是否下载；
- 确认后使用默认浏览器打开 GitHub 上的 `LocalCam-Setup-x64.exe`。

软件不会后台自动检查或静默安装更新，也不使用 GitHub Token。

## 当前限制

- 当前视频链路为 JPEG/WSS，不是 WebRTC/H.264。
- 虚拟摄像头输出固定为 640×480 NV12，尚未提供 1080p。
- 尚未完成所有会议软件、多应用并发和长时间运行兼容矩阵。
- 不包含音频、录像或滤镜。
- Safari 进入后台后受 iOS 系统限制，无法保证持续采集。

## 从源码开发

开发环境要求：

- .NET SDK 10.0.303 或兼容的更新功能带；
- Visual Studio 2022 C++ 桌面开发工具；
- Windows 11 SDK 10.0.26100；
- Inno Setup 6（仅制作安装包时需要）。

构建和测试 .NET 解决方案：

```powershell
dotnet build .\LocalCam.sln --configuration Release
dotnet run --project .\tests\LocalCam.SmokeTests\LocalCam.SmokeTests.csproj --configuration Release
```

生成原生组件、自包含发布目录和安装包：

```powershell
.\scripts\Build-Release.ps1 -GitHubRepository '<owner>/iPhone-localcam' -Version '0.1.0'
```

输出文件位于 `artifacts/release/LocalCam-Setup-x64.exe`。`artifacts/`、`bin/`、`obj/` 和原生生成目录均不会提交到 Git。

更多资料：

- [开发指南](docs/DEVELOPMENT_BOOK.md)
- [当前实现状态](docs/STATUS.md)
- [架构说明](docs/architecture/ARCHITECTURE.md)
- [第三方软件声明](THIRD_PARTY_NOTICES.md)

## 隐私与安全

- 摄像头画面只在当前本地网络中的 iPhone 与 Windows 电脑之间传输。
- LocalCam 不要求账号，不上传视频，不包含遥测或广告 SDK。
- 本地 CA 私钥、证书和用户设置生成在 `%LOCALAPPDATA%\LocalCam`，不包含在源码或 Release 中。
- 配对令牌由运行时随机生成，不会硬编码到源码。
- 手动检查更新会向 GitHub 发送普通 HTTPS 请求；不检查更新时不依赖 GitHub。

## 许可证

LocalCam 使用 [MIT License](LICENSE)。项目包含的 Microsoft Windows-Camera 示例修改代码保留其原始 MIT 许可证和版权声明，详见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
