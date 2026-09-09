# DeskCam · iPhone 到 Windows 纸面预览

此分支是用于 A4 草稿、数学推导和书本截图的 DeskCam，基于 LocalCam 的 MIT 许可代码整理。当前使用 Safari WebCodecs H.264 / WSS 和 Windows Media Foundation 解码，保留原生 MF webcam；不再使用旧 JPEG 直播链。

[中文使用与快捷键](DESKCAM_README.md) · [开发状态](docs/STATUS.md) · [Windows 构建工作流](.github/workflows/build-full.yml) · [许可](LICENSE)

0.7 默认以自动隐藏工具的全窗口画面启动，P 打开控制面板；F 铺满视窗，0 恢复全幅。黑色遮罩和开始/停止也可从电脑控制。主要操作是预览、复制/保存原像素裁剪帧、冻结、锁焦、自动对焦、两组纸面预设、高清抓拍。提供 720p20、1080p15/20/30、4K10 档位；webcam 始终 1080p。手机是否支持摄影控制由能力与回读决定，不能保证每台 Safari 都提供锁焦或 4K。

## 构建与运行

主线为 `codex/deskcam`，推送后 GitHub Actions 构建 self-contained Windows x64 包 `DeskCam-win-x64`，无需用户本机安装 VS Build Tools。解压到固定目录，首次运行 Install-DeskCam.cmd；已注册过的同目录升级无需重复安装，日常用 Start-DeskCam.cmd。不要移动已注册的 VirtualCamera 路径。

本机已交付版本与核验结果记录在外层工作区 DELIVERY.md、verification 和 DeskCam/BUILD.json；普通 CI 包可用程序集版本确认 commit。不能把 CI 或模拟相机测试当作真实 iPhone 光学/校园网验收。

原上游 README 和 STATUS 作为历史保留在 Git 提交记录及外层工作区 _archive/history/product-0.5，避免让后续 AI 误按旧前后摄/JPEG/实验分支说明操作。其余第三方文档与版权声明保留原样，仅用于参考。
