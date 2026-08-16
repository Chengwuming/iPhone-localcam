# LocalCam 开发指南

本文是继续开发时的唯一入口。新智能体先读本文、`docs/STATUS.md` 与 `docs/architecture/ARCHITECTURE.md`，然后检查当前 Git/构建状态；不要把 v0.1 当作已经完成 WebRTC 或虚拟摄像头。

## 产品约束（不可擅自改变）

- iPhone 14 Pro Max 只使用 Safari，不开发或发布 iOS App。
- Windows 11 本地软件；视频、信令、配对与证书均不使用公网或云端服务。
- 允许 Windows 程序自带一个本地 HTTPS 服务；它不是外部服务器。
- 首期目标是 1080p/30fps、低延迟、供 OBS/微信/会议软件选择的 Windows 虚拟摄像头。
- 局域网不等于可信网络：配对 URL 必须继续使用高熵、短期、一次性 token。
- 所有新增模块都要保留将来替换 WebRTC 库、增加录制/滤镜/USB/音频时的边界。

## 仓库地图

```text
src/LocalCam.Server/
  Pairing/       一次性配对令牌
  Security/      本地 CA 与服务器证书
  Network/       LAN 地址选择
  Streaming/     v0.1 JPEG 帧转发
  Presentation/  QR 与两个 Safari/监看页面
src/LocalCam.Contracts/
  FrameIpcContract.cs  Desktop 与原生虚拟摄像头 DLL 共享的二进制 ABI
src/LocalCam.VirtualCamera/
  include/       与 Contracts 对应的 C++ IPC 头文件
  src/           系统能力探针与注册/移除工具
  vendor/Windows-Camera/  微软官方 Media Source 基线及 LocalCam NV12 reader
tests/LocalCam.SmokeTests/  无第三方测试框架的关键行为测试
docs/            本书、状态、架构
```

当前实现选择 ASP.NET Core/.NET 10，是为了先获得真实可运行的本地配对垂直切片；最终 Windows 虚拟摄像头仍可使用 C++20/Media Foundation，是否以 C++ 重写桌面壳应在 v0.2 POC 后再决定，不能在无证据时强行迁移。

`apps/desktop/LocalCam.Desktop` 是当前可运行的 WPF 原生桌面壳。它直接通过 `ClientWebSocket` 接收显示帧，不使用嵌入浏览器；Desktop 已在自身进程内托管 `LocalCam.Server`，从桌面快捷方式启动时无需另开 Server 进程。主界面不常驻显示二维码：通过右上角“连接 iPhone”或托盘菜单打开配对窗口。

## 本地运行与验收

```powershell
Set-Location '<LocalCam 仓库目录>'
dotnet build .\LocalCam.sln --configuration Release
dotnet run --project .\tests\LocalCam.SmokeTests\LocalCam.SmokeTests.csproj --configuration Release
dotnet run --project .\src\LocalCam.Server\LocalCam.Server.csproj --configuration Release
dotnet run --project .\apps\desktop\LocalCam.Desktop\LocalCam.Desktop.csproj --configuration Release
```

推荐只启动原生 `LocalCam.Desktop`；浏览器监看页与单独运行 Server 仅保留作开发诊断。iPhone 与电脑必须在同一可互访网络；本开发机已验证 `Apple Mobile Device Ethernet` 的 USB 网络可作为该网络。校园网启用客户端隔离时，可在配对窗口打开 Windows“移动热点”，让 iPhone 加入电脑创建的私有 Wi-Fi；若 Mihomo TUN 的 strict-route 与 ICS 冲突，应由用户决定是否关闭 TUN，LocalCam 不擅自修改代理或网络设置。

真机验收清单：

1. Windows 页面显示的 LAN IP 正确，不是 VPN、虚拟网卡或 `127.0.0.1`。
2. iPhone 扫证书 QR，可下载并按页面步骤完全信任 `LocalCam Local CA`。
3. 扫开始连接 QR，Safari 显示 LocalCam 页面，允许后置摄像头权限。
4. Windows 预览从“等待”变为收帧，默认观察约 20 fps；点击电脑端“30 FPS”后观察实际帧率与稳定性。
5. 分别用手机页面和电脑端“前置摄像头/后置摄像头”按钮切换，确认预览切换成功。
6. 关闭 iPhone 页面，Windows 预览停止；重新连接时必须重新生成 QR。
7. 拔掉路由器 WAN（保留 Wi-Fi/LAN）后重复第 3–4 步，验证不依赖公网。
8. Windows“移动热点”验收：在配对窗口点击“打开移动热点设置”并由用户开启热点 → iPhone 加入该热点 → 回到 LocalCam 点击“刷新” → 下拉框显示“Windows 移动热点”并被自动选中 → 扫描 QR，重复第 3–4 步。

## 证书说明与故障分流

首次 iPhone 使用的正确顺序是：点击右上角“连接 iPhone”后展开“首次使用”并扫描证书引导 QR → 安装描述文件 → 在 iOS 证书信任设置中开启完全信任 → 扫描该窗口默认显示的一个 HTTPS 连接 QR。日常使用只需扫描默认的一个连接 QR。直接打开 HTTPS 二维码而未信任证书时，Safari 拒绝访问或 `getUserMedia` 失败是预期行为。

- 证书下载失败：确认 iPhone 能访问 `http://<电脑IP>:29100/`；检查防火墙是否阻断入站端口 29100。
- HTTPS 页面失败：确认完全信任的 CA 名称为 `LocalCam Local CA`，并检查端口 29101。
- 二维码连接失败：当前配对 token 可能过期（5 分钟）或已消费；刷新 Windows 本地预览生成新 token。
- 没有摄像头权限：在 iPhone 设置中给 Safari 开启摄像头权限，再重新加载页面。
- PC 显示错误 LAN IP：配对窗口的下拉框可手动选择当前网络。已启用的 Windows 移动热点优先，其次 iPhone USB；VMware、Hyper-V、VirtualBox 和隧道网卡被降级。移动热点的默认 `192.168.137.1` 已预置到本地 HTTPS 证书，以支持在服务启动后再开启热点。

## 版本路线与验收门槛

### v0.1：Local Preview（当前）

只有真机清单通过才标记完成。JPEG/WSS 是刻意受限的验证通道：20 fps 为默认值、30 fps 是 USB 真机验证模式；它不可宣称低延迟高清视频或 60 fps 完成。

### v0.2：WebRTC Receiver

目标：Safari 的 `RTCPeerConnection` 与 Windows 接收端建立仅 LAN candidate 的 WebRTC 连接，H.264 访问单元可被接收。

- 先建立 `SignalingProtocol v1`：`session.hello/accept/reject`、`rtc.offer/answer/ice`、`error`、`ping/pong`，每条消息含协议版本与 session id。
- 定义 `IRealtimeTransport`；可选择 libdatachannel，但其头文件与具体类型不得泄漏到 Contracts。
- 禁止配置公网 STUN/TURN；记录生成的 ICE candidates 与最终 candidate pair，以便验证 LAN-only。
- 接收 H.264 后创建 `IVideoDecoder`，优先 Media Foundation H.264 Decoder MFT，并统一输出 NV12 `VideoFrame`。
- 验收：真机 720p30 稳定 10 分钟；断开/恢复 Wi-Fi 可重连；日志能确认没有公网候选。

### v0.3：FrameHub + Desktop Preview

目标：Decoder 发布 `VideoFrame` 到线程安全 FrameHub，独立 PreviewSink 使用 D3D11 渲染并替换 WPF 壳内当前的 JPEG `BitmapImage`。录制、截图、滤镜只能作为 sink/filter 加入，不能直接改 WebRTC 接收。

### v0.4：Virtual Camera POC（主链路已贯通）

目标：用微软 `IMFVirtualCamera` 官方模型建立独立 `LocalCam.VirtualCamera` Media Source DLL。先在测试图案源上让 OBS 枚举到 `LocalCam Camera`，再接 IPC，最后接 FrameHub。

- Desktop 与 DLL 之间已提供 `LocalCam.FramePipe.v1` 最新帧请求/响应通道；当前也承担 CaptureService 无法打开用户 mapping 时的 NV12 后备传输。
- Media Source 注册保留在安装层；桌面启动一个持有 `MFVirtualCameraLifetime_Session` 的原生会话管理器，软件退出或崩溃后摄像头从系统设备列表自动移除。
- NV12 帧走 memory-mapped ring buffer（至少 4 slots、format/version/sequence/timestamp）。
- 虚拟摄像头只读取最新完整帧，永不排队等旧帧。
- 必须验证卸载、服务重启、OBS/微信同时打开、Desktop 意外退出时的失败模式。

当前已固定 `LocalCam.Contracts/FrameIpcContract.cs` 与 `LocalCam.VirtualCamera/include/FrameIpcContract.h`：`LCAM` magic、v1、4 slots、NV12、64-byte ring/slot headers、`Local\\LocalCam.FrameRing.v1`、帧可用事件名与 `LocalCam.FramePipe.v1`。生产者先写 payload、最后发布 `Sequence`；消费者只采纳稳定、640×480 NV12 且 3 秒内的新帧，并在单次命名管道读取失败时复用仍新鲜的最后完整帧。x64 Release Media Source 已构建；实际运行已验证模拟手机帧、镜像翻转、断开回退和真实 iPhone 画面。下一步继续扩充多应用并发和长期稳定性验收。

### v1.0

1080p30、配对与证书 UX、重连、诊断、MSI 安装/卸载、真机矩阵完成后才能命名 v1.0。之后再评估音频、录制、旋转/镜像/缩放、60fps、USB 网络、滤镜。

## iPhone 前后台边界

在“iPhone 只使用 Safari、不安装原生 App”这一硬约束下，不能承诺返回桌面后继续使用摄像头，也不能做微信式的摄像头 PiP 小窗，更不能在 USB 插入时由 PC 无感唤起 Safari。它需要原生 App 通过 AVFoundation 的视频通话 PiP 和多任务摄像头访问能力实现。Safari 方案应在 iPhone 离开前台时明确显示“传输已暂停”，回到 Safari 后由用户恢复连接；不得以后台持续采集或自动唤起作为验收项。

## 编码与安全规则

- 任何网络控制消息必须有协议版本、类型、session id，并在进入业务层前完成长度/类型校验。
- 密钥、视频帧、配对 token、IP 地址不可上传或写入遥测；默认不做遥测。
- 诊断日志若加入，应是本地可清除文件，且不得记录完整 token、SDP、摄像头帧或证书私钥。
- 修改 CA、端口、协议或 IPC layout 时，更新本书、架构图、版本号与迁移说明。
- 编码后最少运行 Release build、烟雾/单元测试和真实场景验证；不相关的临时文件不得保留在仓库。
