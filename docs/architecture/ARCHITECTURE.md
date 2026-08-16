# 架构与模块边界

## 最终目标数据流

```text
iPhone Safari -- WebRTC/H.264 --> RealtimeTransport --> Decoder --> FrameHub
                                                               |         |
                                                        PreviewSink   Frame IPC
                                                                         |
                                                            Virtual Camera DLL
                                                                         |
                                                           Windows Frame Server
```

## 当前开发版数据流

```text
iPhone Safari -- HTTPS/WSS JPEG --> FrameRelay --> LocalCam.Desktop
                                                   |          |
                                              WPF Preview  JPEG→NV12
                                                              |
                                           shared-memory ring (preferred)
                                            or latest-frame named pipe
                                                              |
                                              Virtual Camera Media Source
                                                              |
                                                Windows Camera / OBS / meetings
```

当前 JPEG/WSS 仍是验证通道；为了先形成完整可测的系统链路，桌面端临时将 JPEG 解码结果转为 640×480 NV12。后续 WebRTC/H.264 解码器应直接发布 NV12，替换这段 CPU 转换，但保持 IPC ABI 与虚拟摄像头消费者不变。

## 未来稳定接口

`Contracts` 模块只放值对象、事件与接口，不能依赖 WinUI、WebRTC、OpenSSL、Media Foundation 或网络库。

```cpp
struct VideoFrame { FrameId id; Timestamp timestamp; VideoFormat format; Buffer buffer; };
interface IVideoFrameSink { void OnFrame(const VideoFrame& frame); }
interface IRealtimeTransport { Start(); Stop(); SetVideoCallback(...); }
interface IVideoDecoder { Configure(...); Decode(...); Flush(); }
```

实现替换只可向下依赖：`WebRTC -> Decoder -> FrameHub -> Sinks`。UI 只能订阅状态和 PreviewSink，不能持有解码器、Socket 或虚拟摄像头对象。

## 进程边界

Windows 虚拟摄像头 Media Source 可能由 Camera Frame Server 托管，不能和桌面 UI 假设同一进程。届时采用：

```text
LocalCam.Desktop.exe  -- named-pipe control + shared-memory ring buffer --> LocalCam.VirtualCamera.dll
```

ring buffer 必须只让消费者获取最新帧，消费者慢时丢帧而非堆积，以避免实时延迟持续增长。

开发机实测 Windows“相机”的 CaptureService 可能无法从其宿主安全边界直接打开桌面用户的 named mapping。因此 POC 保留共享内存首选路径，同时提供 `LocalCam.FramePipe.v1` 请求/响应后备：Media Source 每次只请求当前最新 NV12；Desktop 不推送队列。管道仅在本机存在，ACL 限定当前用户、SYSTEM 与 LocalService。
