# Wallerpaper

> 对标 Wallpaper Engine / Lively 的 Windows 动态壁纸引擎  
> C# · WinUI 3 · .NET 8 · Windows App SDK 1.7 · Win32 P/Invoke  
> 状态：**Phase 0–7 全部跑通**，本机日常使用的完整实现（五类壁纸 + 工具链 + 托盘 + 单文件安装器）；尚未做代码签名与自动更新。

## 功能

### 壁纸类型

| 类型 | 渲染方式 | 要点 |
|---|---|---|
| 动态 | libmpv 进程内嵌入（`wid`）、D3D11VA 硬解 | 音量 / 倍速 / 静音实时下发；暂停释放显存；满帧与限帧曲线 |
| 静态 | 颜色调整 + Real-ESRGAN 超分后写系统壁纸 | 亮度 / 对比度 / 高光 / 色相 / 色温 / 曝光 / 锐化 / RGB 分通道 |
| Web | WebView2 raw controller + WinForms 消息循环 | 鼠标透传、JS bridge、域名 allowlist；渲染进程崩溃自动重建，最多 3 次后显示兜底页 |
| 场景 | Silk.NET D3D11 + GLSL + QuickJS | 图层合成、粒子上限、音频 FFT；可编辑图层位置 / 缩放 / 旋转 / 透明度 / 粒子数并另存为新场景 |
| 组合 | 序列播放 | 多个壁纸依次播完轮换，第一个结束后接第二个 |

### 管理界面（WinUI 3）

- 分类导航：动态 / 静态 / Web / 场景 / 组合 / 回收站 / 设置 / 超分 / AI 补帧
- 缩略图网格 + 收藏 + 搜索；双击卡片直接应用，右侧属性面板实时改音量、倍速、渲染参数
- 托盘图标、快速切换壁纸、暂停 / 播放；开机自启用 `--minimized` 静默进托盘
- 多显示器逐屏分配；目录可以是文件夹、单个文件，场景识别 `scene.json`
- 配置：`%LOCALAPPDATA%\Wallerpaper\shell-config.json`（原子写入，损坏回退默认）

### 工具链

- 视频超分（Real-ESRGAN）、AI 补帧（RIFE）、自动黑边裁剪、视频裁剪、导出 MP4
- 长任务实时显示进度与预计剩余时间
- 派生文件按 `AutoCleanDays` 自动清理（壁纸库、回收站里的文件始终保留），崩溃转储只留最新一个；
  设置页可查看占用、立即清理、打开产物目录、查看日志

### 与桌面 / 系统的衔接

- **桌面底图跟随**：启动 / 恢复播放时把系统桌面壁纸换成当前壁纸的首帧
  （`cache/desktop-frame`，只保留一张；按目标显示器分辨率 + 该壁纸的 fit 方式在 t=0 取帧），
  渲染层就绪前露出的是即将播放的画面；暂停 / 退出用快照恢复用户原壁纸
- **切换 = 预加载后交换**：调度器先把下一个渲染器隐藏着启动，等它报告首帧已上屏，才关掉旧的渲染器；
  旧画面全程可见，切换过程中不重设系统壁纸。新渲染器稳定约 1 秒后才把系统壁纸同步成新壁纸的首帧
  （实测 Explorer 此时重插的 WorkerW 仅遮挡约 8ms，肉眼不可见），所以桌面设置里也能看到壁纸跟着变
- **首帧前隐藏**：渲染宿主先挂到 WorkerW 但保持隐藏，首帧就绪后再显形，启动不闪黑窗口
- **z-order 守卫**：系统壁纸被重设时 Explorer 会新插一层 WorkerW 盖住渲染窗口，
  宿主收到 `WM_SETTINGCHANGE` 立即把自己排回正确位置，并带 250ms 轮询与 1s 看门狗
- **锁屏跟随**：可选把当前壁纸（静态图或首帧）设为 Win+L 锁屏图
- **策略与兜底**：全屏检测三档策略（continue / pause / stop）、最大化触发、AC / 电池分档 FPS、
  显存上限、看门狗 + Job Object；截图 / 录屏工具的瞬时遮罩不会触发暂停

## 快速开始

要求：Windows 10 1809+ / Windows 11；构建需要 .NET 8 SDK（用户侧是自包含发布，不需要装运行时）。

一步构建发布 + 打包安装器（输出到 `artifacts/`，会先结束正在运行的实例）：

```powershell
pwsh -File tools\build-release.ps1
```

只构建主界面用于调试：

```powershell
dotnet build samples\WallerpaperShell\WallerpaperShell.csproj -c Debug
samples\WallerpaperShell\bin\Debug\net8.0-windows10.0.19041.0\win-x64\WallerpaperShell.exe
```

要真正接管桌面，主界面同目录还需要：`WallerpaperHost.exe`、`VideoWallpaperHost.exe`、`WebWallpaperHost.exe`、
`SceneWallpaperHost.exe`、共享的 `Wallerpaper.RenderHost.WorkerW.dll`、`libmpv-2.dll`、`WebView2Loader.dll`。
只替换单个 dll（尤其漏掉 `resources.pri`）会导致 XAML 资源加载失败。

安装包：`artifacts/WallerpaperSetup.exe`（单文件自解压，由 `tools/build-release.ps1` 生成，属于可再生的开发产物，
每次构建会被重写；设置页的自动清理也会回收旧内容）。

## 仓库结构

```text
samples/
  WallerpaperShell/          # WinUI 3 主界面：壁纸库、属性、设置、超分、补帧、托盘、首帧底图
  WallerpaperHost/           # 调度器：全屏检测、策略、多显示器、看门狗、电源
  VideoWallpaperHost/        # libmpv 渲染子进程
  WebWallpaperHost/          # WebView2 渲染子进程
  SceneWallpaperHost/        # D3D11 场景渲染子进程
  Shared/WorkerWRuntime/     # 渲染宿主共用的 WorkerW 挂靠 + z-order 守卫
  Shared/Wallerpaper.Diagnostics/
  Installer/WallerpaperSetup/# 单文件安装器
  WorkerW/ VideoWallpaperParent/ WebWallpaperParent/ SceneWallpaperParent/  # Phase 0 探针与父进程样例
docs/                        # 架构、ADR、测试报告、兼容性清单、资源指标
tools/                       # 构建发布、UI 流程测试、稳定性统计、超分 / 补帧模型获取
```

设计文档里的 `src/` 分层（App / Core / Contracts / Interop / RenderHost …）是目标结构；
当前可运行代码都按上面的 `samples/` 组织。

## 文档

- 架构与选型（含 Lively 源码对照、API 验证表）：[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)
- 决策记录索引：[`docs/adr/README.md`](docs/adr/README.md)
- 稳定性测试：[`docs/TEST-REPORT.md`](docs/TEST-REPORT.md)；用户流程测试：[`docs/PHASE6-TEST-REPORT.md`](docs/PHASE6-TEST-REPORT.md)
- 兼容性检查清单：[`docs/COMPATIBILITY-CHECKLIST.md`](docs/COMPATIBILITY-CHECKLIST.md)
- 资源占用指标：[`docs/RESOURCE-METRICS.md`](docs/RESOURCE-METRICS.md)
- 产品化：[`docs/PRODUCTIZATION.md`](docs/PRODUCTIZATION.md)
- 各模块说明：`samples/*/README.md`
- Visual Studio 解决方案：[`Wallerpaper.sln`](Wallerpaper.sln)

## 选型：设计文档 vs 当前实现

| 项 | 设计文档 | 当前实现 |
|---|---|---|
| 运行时 | .NET 10 LTS | **.NET 8**（自包含发布，不要求用户装运行时；升 .NET 10 待做） |
| UI | WinUI 3 非打包，Windows App SDK 自包含 | 一致（Windows App SDK 1.7.250606001） |
| 场景渲染 | Vortice.Windows D3D11 | **Silk.NET** Direct3D11 2.23（Vortice 作为备选未采用） |
| 进程模型 | UI 进程 + 每「壁纸实例 × 显示器」一个渲染宿主 | 一致（`WallerpaperHost` 编排，子进程渲染） |
| IPC | stdin/stdout 管道 + 长度前缀 + MessagePack | 一致，实际承载为**行式 JSON**（便于抓日志调试） |
| 视频 | libmpv `wid` 嵌入 | 一致 |
| Web | WebView2 raw controller + WinForms 消息循环 | 一致 |
| 桌面挂靠 | 经典 WorkerW + Win11 raised desktop 双路径 | 一致，另加 z-order 守卫与首帧前隐藏 |
| 安装 / 更新 | Velopack 自动更新 | 单文件自解压安装器；代码签名、自动更新未做 |

## 已知限制

1. 开发环境是单显示器，多显示器枚举与逐屏分配已实现，但热插拔未实机验证。
2. Web 缩略图是确定性占位图（不启隐藏 WebView2）；场景缩略图取首帧图片，不是 GPU 实时渲染。
3. 未做代码签名、MSIX 打包、自动更新。
4. Web 壁纸的音量 / 倍速只作用于页面内 `<video>` / `<audio>`，自定义音频总线需页面自行处理。
5. 超分 / 补帧依赖本机模型文件：`tools/fetch_esrgan.py`、`tools/fetch_rife.py`。
6. 自启动路径依赖当前 exe 位置，移动安装目录后需要在设置里重新保存一次。
7. `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 写入，无需管理员权限。

## 参考

- Lively（GPL-3.0，可复用）：<https://github.com/lively-community/lively>
- 调研基线 commit：`25a0a5f4ed1988ff42117d3a17feb321062237fe`
- .NET 支持策略：<https://learn.microsoft.com/en-us/dotnet/core/releases-and-support>
- Windows App SDK 非打包部署：<https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/deploy-unpackaged-apps>
- WinUI 3：<https://learn.microsoft.com/en-us/windows/apps/winui/winui3/>
- WebView2：<https://learn.microsoft.com/en-us/microsoft-edge/webview2/>
- Silk.NET：<https://github.com/dotnet/Silk.NET>
- libmpv：<https://github.com/mpv-player/mpv>
- Real-ESRGAN（超分）：<https://github.com/xinntao/Real-ESRGAN>
- RIFE（补帧）：<https://github.com/megvii-research/ECCV2022-RIFE>

## 许可证

本项目采用 **GPL-3.0**。复用 Lively 代码时必须保留版权头、LICENSE 和来源 commit；
libmpv / FFmpeg 的源码与许可以及第三方 NOTICE 需随分发提供。
