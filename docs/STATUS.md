# DeskCam 产品状态

2026-09-10：0.7.0 代码提交 `24721164f0e317f43fbcbfc7bd82c3034af1a29c`，GitHub Actions 构建 [34416312202](https://github.com/Chengwuming/iPhone-localcam/actions/runs/34416312202) 全部成功。本文件可在代码构建后单独更新，实际运行包以程序集版本和外层 DeskCam/BUILD.json 为准。

主线是 codex/deskcam。Safari WebCodecs H.264 / WSS → Windows MF/DXVA → 原像素预览、裁剪、复制、保存；MF webcam 单独固定 1080p。没有 JPEG/WebRTC fallback。

0.7 默认全窗口预览，边缘浮动工具自动隐藏，P 打开相机抽屉；F 等比裁切铺满，0 恢复全幅。手机遮罩/开始/停止可远程控制，遮罩记住上次状态。暗色下拉框、滑杆和焦点反馈统一；窄窗口抽屉避开工具栏，更多操作与抽屉互斥。

摄影权限由 iOS 控制，主屏幕 Web App 可能重复询问。Safari 单站点设置相机允许后，可以在添加到主屏幕时关闭 Open as Web App，使用浏览器入口。系统相机拍照需要手机手势，高清抓拍 H 可从电脑操作。

实际验证位于外层 verification/2026-09-10-preview：真实视频链路、默认预览、竖屏几何、控制/遮罩持久化、4K 和 30fps、保存/复制、窄窗口。Chrome 合成摄像头与模拟摄影能力不是 iPhone 光学或校园网验收。旧状态已保存在 Git 历史与外层 _archive。

使用见 ../DESKCAM_README.md；完整交付/恢复记录由外层 HANDOFF.md、DELIVERY.md 和 docs 管理。
