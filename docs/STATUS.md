# LocalCam 开发状态

最后更新：2026-08-16

## 已完成：v0.1 Local Preview

- 单仓库 .NET 10 解决方案与模块目录。
- 本地 CA：首次启动时在 `%LOCALAPPDATA%\LocalCam\certificates` 生成 CA；每次启动生成包含当前局域网 IP 的 HTTPS 服务证书。
- 安全引导：HTTP 端口 `29100` 只提供证书安装引导；摄像头网页只在 HTTPS 端口 `29101` 提供。
- 一次性配对：128-bit session id、256-bit token、5 分钟有效期、固定时间令牌比较、连接后立刻作废。
- iPhone Web 客户端：后置摄像头优先、720p、20 fps 默认/30 fps 可选、`playsinline`、JPEG 二进制 WSS 发送；手机页面可切换前后置。
- Safari 权限复用：手机页面说明如何把当前站点的摄像头权限设为“允许”；成功授权后按 HTTPS origin 记录，下次页面会直接尝试启动。首次授权、权限仍设为“询问”或切换到不同 IP origin 时，iOS/Safari 仍可能要求确认，网页不能绕过系统隐私提示。
- Windows 本机预览页：本地显示二维码、复制链接、WSS 收帧与 FPS 状态，并可遥控 iPhone 的前后置与 20/30 fps。
- 原生 Windows 桌面壳：WPF `LocalCam.Desktop` 直接以 `ClientWebSocket` 接收 JPEG 帧并原生显示预览与控制；不嵌入浏览器。iPhone 连接改为右上角“连接 iPhone”独立窗口，不再占用首页。
- 桌面一体化启动：`LocalCam.Desktop` 在自身进程内启动并管理本地 HTTPS/WSS 服务，快捷方式不再要求用户另开 `LocalCam.Server`。监看 WebSocket 会自动重连，隐藏到托盘或开机最小化启动时也继续收帧。
- 托盘与设置：托盘右键使用独立 WPF 圆角菜单窗口，不再混用易闪退的 WinForms `ContextMenuStrip` 或无宿主 WPF `ContextMenu`；提供显示、连接 iPhone、设置和退出。设置可选择关闭按钮是退出还是隐藏到托盘，并可配置当前用户开机启动。
- 窗口与后台生命周期：主窗口会保存上次正常位置、大小和最大化状态，并在显示器布局变化后进行可见范围校验；关闭到托盘或 `--minimized` 启动时卸载预览帧引用、使用 640 像素后台解码、复用虚拟摄像头转换缓冲并整理工作集，连接服务和虚拟摄像头输出保持运行。
- 校园网替代连接：配对窗口可打开 Windows“移动热点”设置、刷新网卡并显式选择连接地址；检测到 Wi-Fi Direct 私有地址后优先生成热点 QR。移动热点实机连接仍待用户开启热点后验收。
- 手机热点连接：Windows 加入手机提供的 Wi-Fi 热点时会优先识别并标注“当前使用：手机热点（Wi-Fi）”。
- 画面控制：完整显示（不裁边）/铺满窗口（裁边）、手机与电脑两端前后摄像头、缩放请求。缩放仅在 Safari 暴露该镜头的 `zoom` capability 时可用。
- 摄像头镜像：桌面端可即时开启或关闭水平镜像，同时作用于本机预览和虚拟摄像头输出，不影响手机端采集。
- 帧中继：最大帧 2 MiB、监看客户端断开清理、只转发最新到达的帧；不建立积压队列。
- JPEG 实时性保护：iPhone 发送画面限制在最长边 1280 像素、JPEG 质量 0.52；WebSocket 积压超过 512 KiB 时主动丢帧，避免弱热点链路越传越慢。
- 烟雾测试：新 token、错误 token、一次性消费、过期与清理。
- 虚拟摄像头 POC：`LocalCam.Desktop` 将 JPEG 实帧转换为 640×480 NV12，发布到 4-slot 共享内存；若 Windows CaptureService 的安全边界无法打开该 mapping，则通过受限 ACL、支持多个并发客户端的本地命名管道按请求返回最新帧。原生 Media Foundation Source 同时支持 NV12 直拷贝与 RGB32 转换。`LocalCam Camera` 改为会话生命周期，只在 LocalCam 运行时接入系统，退出或桌面进程异常结束后自动移除。
- 断开保护：桌面端收到 iPhone 断开状态后清空 latest sequence；原生读取器会缓存共享内存或管道取得的最后一张完整 NV12 帧，瞬时读取失败不会露出测试图。明确清空或帧失效时只显示静态纯黑兜底，不再显示蓝黑测试图。

## 已验证

2026-08-15 在开发机完成：

```text
dotnet build LocalCam.sln --configuration Release   成功，0 warning / 0 error
LocalCam.SmokeTests                                 通过
HTTP bootstrap endpoint                             200，包含证书下载链接
HTTPS /api/session                                  200，返回 pair link 与本地生成二维码
```

2026-08-16 追加验证：Release .NET solution 0 warning / 0 error，冒烟测试通过；MSVC x64 Release Media Source 构建成功；系统能力探针枚举物理摄像头与 `LocalCam Camera`。在 Windows“相机”中完成无帧兜底、模拟手机帧、停止后自动回退和镜像测试。随后用真实 iPhone 扫码，桌面端稳定接收约 19–23 fps，Windows“相机”成功显示 iPhone 实景。说明 iPhone JPEG→桌面预览→NV12→命名管道→系统虚拟摄像头、实时缩放、镜像与断开降级均已贯通。

2026-08-16 后续验证：桌面内嵌服务在 WLAN 地址正常监听，隐藏启动仍保持 monitor 连接；会话型虚拟摄像头在软件启动时出现，进程停止后只剩物理摄像头。原生逐帧连续性探针以模拟手机帧连续读取 `LocalCam Camera` 3000 帧，结果 `fallback-black=0`。托盘菜单的显示、连接 iPhone、设置、退出四个入口均已验证；退出后无 Desktop、Registrar 或本地服务进程残留。

2026-08-16 发布验证：只复制 Git 将公开的源文件到全新目录后，原生 Media Source、CMake 工具、Release .NET solution、烟雾测试、自包含 publish 和 Inno Setup 安装包全部成功。安装包在默认安装目录实际安装并启动后，HTTP/HTTPS 端口正常监听，`LocalCam Camera` 可枚举，用户设置与本地 CA 证书哈希保持不变。

实机测试覆盖“电脑加入 iPhone 个人热点”的方式，程序能够识别并标注为“手机热点（Wi-Fi）”。另外也能识别 iPhone USB 网络 `Apple Mobile Device Ethernet`；配对窗口允许手动选择网卡。

## 明确未完成

| 范围 | 状态 | 原因/下一步 |
| --- | --- | --- |
| WebRTC H.264 接收 | 未开始 | v0.1 用 JPEG/WSS 先验证网页与证书；替换为 `IRealtimeTransport` 实现。 |
| Media Foundation 解码 | 未开始 | 接收 H.264 access unit 后输出 NV12 `VideoFrame`。 |
| FrameHub | 未开始 | 先定义 Contracts，再让 decoder 发布帧。 |
| Windows 虚拟摄像头 | POC 已贯通 | 正式设备名为 `LocalCam Camera`；Windows“相机”中的 iPhone 实帧、镜像、瞬时读取容错和断开回退均已验收。仍需多应用并发和长期稳定性验收。 |
| 高性能桌面预览 | 部分完成 | WPF 原生 UI 已有；当前仍接收 JPEG，D3D11/NV12 PreviewSink 留待 WebRTC/Decoder 完成后替换。 |
| 安装包 | 首版完成 | Inno Setup x64 自包含安装包已能安装应用并注册虚拟摄像头；卸载保留用户设置和本地证书。 |
| iPhone 回桌面持续采集 / 摄像头 PiP / USB 自动唤起 | 不可由 Safari 实现 | Safari 网页会受 iOS 前后台生命周期约束；“微信式”后台摄像头与视频通话 PiP、USB 插入后无感唤起均需要原生 iOS App 的能力。 |
| 音频/录制/滤镜 | 未开始 | 都不是 v0.1 范围。 |

## 下一次最小任务

进入长时间运行与多应用并发验收，再决定是否启动 `v0.2 WebRTC Receiver` 以降低 JPEG/WSS 链路的带宽和延迟。
