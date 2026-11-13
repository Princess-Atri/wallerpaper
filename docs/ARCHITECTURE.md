# Wallerpaper 设计说明书 v0.1

> 对标 Wallpaper Engine / Lively 的 Windows 动态壁纸引擎  
> 技术栈基线：C# + WinUI 3 + .NET 10 LTS + 原生 Win32 P/Invoke  
> 文档状态：Draft for review（架构/路线/风险/选型四合一）  
> 调研基线：Lively `core-separation` 分支，commit `25a0a5f4ed1988ff42117d3a17feb321062237fe`  
> 许可：GPL-3.0（已确认）；Lively 代码可按 GPL 复用，但必须保留版权/许可/来源。  
> 说明：本文中的“我们”指 Wallerpaper 项目。

---

## 0. 先读这一页

### 0.1 六个必须先接受的事实

1. **Windows 没有公开的“动态壁纸 API”。** `IDesktopWallpaper` 和 `SystemParametersInfo(SPI_SETDESKWALLPAPER)` 只能设置静态图片；把窗口挂到 `Progman`/`WorkerW` 下面，是 Explorer 的未公开窗口层级用法，不是 Microsoft 承诺的扩展点。Lively 走这条路；Wallpaper Engine 的闭源实现未公开，但同类产品普遍依赖这一层级。
2. **`0x052C` 是未文档化的 Progman 消息。** 它能在 MSDN 之外被验证的唯一方式是 Lively 源码（`WinDesktopCore.SetupDesktopLayer()`），不能写成“系统 API”。本文会明确标注所有此类行为。
3. **Windows 11 的 “raised desktop” 是另一条路径。** 当 `Progman` 带 `WS_EX_NOREDIRECTIONBITMAP`、`SHELLDLL_DefView` 变成 `WS_EX_LAYERED` 子窗口时，壁纸要作为 **Progman 的 layered child** 挂在 DefView 下方，而不是简单挂到 WorkerW。Lively 对这条路径有完整实现。
4. **本项目选择 GPL-3.0，Lively 也是 GPL-3.0。** 因此可以按 GPL 复用 Lively 代码，但必须保留版权/许可/来源，衍生作品整体 GPL-3.0。`libmpv`/FFmpeg 是 GPLv2+ 系，需确认与 GPLv3 兼容、随附源码/许可与第三方 NOTICE。
5. **.NET 8 在 2026 年 11 月结束支持。** 如果产品在 2026 年底之后还要维护，直接上 **.NET 10 LTS**（支持到 2028 年 11 月）；.NET 8 只适合做短期原型。
6. **WebView2 Runtime 是 Web 壁纸的硬前置；WinUI 3 需要 Windows App SDK（自包含或安装器部署）；VC++ Redistributable 只在 libmpv/原生依赖需要时安装。** 安装器必须检测/引导 WebView2 与 Windows App Runtime，否则 Web 壁纸/主程序在干净机器上可能打不开。

### 0.2 结论摘要

| 议题 | 结论 |
|---|---|
| UI 框架 | **WinUI 3（已确认）**；Fluent/Mica 开箱即用；托盘/热键/子窗口用 Win32 互操作；Windows App SDK 自包含或安装器部署 |
| 运行时 | 目标 **.NET 10 LTS**；.NET 8 仅用于过渡/原型 |
| 进程模型 | 主 UI/编排进程 + **每个“壁纸实例 × 显示器”一个 RenderHost 子进程**；`span`/`duplicate` 作为例外模式 |
| IPC | **stdin/stdout 匿名管道 + 4 字节长度前缀 + MessagePack**；不用 localhost 端口；命名管道仅作调试/带外通道 |
| 视频 | `libmpv-2.dll` 进程内嵌入（`wid`）；用 `IVideoEngine` 抽象保留 `mpv.exe` 兜底；GPL-3.0 兼容审计 |
| Web | WebView2（优先 raw `CoreWebView2Controller` + WinForms 消息循环宿主）；CEF 仅在需要固定 Chromium/编解码时考虑 |
| 2D/Shader | D3D11；**首选 Vortice.Windows**，Silk.NET 可用但 DXGI/D3DCompiler/WIC 互操作工作更多 |
| 桌面挂靠 | 经典 WorkerW + Win11 raised desktop 双路径；能力探测 + 失败兜底 + 静态快照壁纸 |
| 全屏暂停 | `SHQueryUserNotificationState`（独占全屏）+ 前景窗口覆盖检测（无边框全屏）+ 去抖/应用规则 |
| 显存释放 | 分级：Pause → Media Unload → Host Terminate；`TrySuspendAsync`、`IDXGIDevice3::Trim`、`ResizeBuffers(0)`、杀进程 |
| Explorer 重启 | `TaskbarCreated` + `EVENT_OBJECT_DESTROY` + Explorer PID 比对 + 指数退避 + 静态快照兜底 |
| 安装/更新 | WinUI 3 非打包 + .NET 10/Windows App SDK 自包含 + Velopack；WebView2 Runtime 检测；MSIX 只用于商店分发 |
| 许可证 | **GPL-3.0（已确认）**；可复用 Lively 代码，保留版权/来源；libmpv/FFmpeg 做 GPL 兼容与源码分发审计 |

### 0.3 对原始需求的 7 处修正（建议）

| 原需求 | 建议修正 | 原因 |
|---|---|---|
| “每个壁纸独立渲染子进程” | 改为“每个 **(壁纸实例, 显示器)** 一个 RenderHost” | 多屏独立壁纸时，进程边界必须落到显示器；否则一个进程内多窗口仍会共享崩溃域与 GPU 设备 |
| “图标点击穿透” | 明确为“图标/桌面右键/框选始终由 Explorer 处理；壁纸默认不接收鼠标” | 正确实现靠 z-order 在 `SHELLDLL_DefView` 之下，不是 `WS_EX_TRANSPARENT` 万能 |
| “全屏游戏自动暂停释放显存” | 增加“分级释放 + 静态快照兜底 + 恢复冷启动预算” | 仅 `pause=true` 不释放显存；杀进程必须让桌面仍是同一画面，否则黑屏 |
| 未提“WebView2/VC++ 前置” | 纳入安装器与首次启动检查 | 干净机器缺 Runtime 时 Web 壁纸不可用 |
| 未提“安全模式/崩溃预算” | 纳入 1.0 验收 | 一个坏壁纸/坏驱动不能造成启动崩溃循环 |
| 未提“许可证决策” | **已确认 GPL-3.0** | 可复用 Lively 代码；libmpv/FFmpeg 需 GPL 兼容审计与源码分发 |
| 未提“应用型壁纸（exe）” | 1.0 不支持 | 任意代码执行 = 与用户同权限，和“壁纸”信任模型不匹配；Lively 支持，但我们不建议 |

---

## 1. 模块架构

### 1.1 设计原则

1. **UI 与渲染彻底分离。** 主进程不创建任何壁纸渲染 surface；RenderHost 不引用 WinUI/UI。
2. **崩溃域最小化。** 一个壁纸实例一个进程；WebView2/Chromium 自己的进程树再套一层；Job Object 保证主进程死亡时子进程全灭。
3. **控制面与数据面分离。** IPC 只传命令/状态，不传帧；帧由各渲染器直接进自己的 HWND/GPU。
4. **桌面挂靠是“策略”，窗口操作是“执行”。** 主进程负责发现 WorkerW/DefView/模式；RenderHost 负责对自己窗口做 `SetParent`/样式/定位，减少跨进程窗口操作。
5. **任何时刻都有静态兜底。** 每个显示器保留一张与当前壁纸匹配的静态快照，作为暂停/卸载/崩溃/Explorer 重启期间的桌面背景。
6. **未文档化行为必须可探测、可降级、可观测。** WorkerW、`0x052C`、`Chrome_WidgetWin_*` 都按“best-effort”处理，失败不能黑屏或崩溃。

### 1.2 进程与信任边界

```text
┌──────────────────────────────────────────────────────────────────────────────┐
│ Wallerpaper.App.exe  （主进程：WinUI 3 + 编排，.NET 10）                    │
│                                                                              │
│  ┌───────────────┐  ┌────────────────┐  ┌────────────────────────────────┐  │
│  │ WinUI Shell   │  │ Tray / Hotkey  │  │ Hidden Message Window (Win32)  │  │
│  │ Views + VMs   │  │ SingleInstance │  │ TaskbarCreated / Display /     │  │
│  └───────┬───────┘  └───────┬────────┘  │ Power / Session / CopyData    │  │
│          │                  │           └───────────────┬────────────────┘  │
│  ┌───────▼──────────────────▼───────────────────────────▼────────────────┐  │
│  │ Application Core（无 UI 依赖，可日后抽成 Core 服务）                   │  │
│  │ Library │ Displays │ Desktop │ Playback │ Input │ Diagnostics         │  │
│  └───────┬──────────────────┬──────────────────┬─────────────────────────┘  │
│          │                  │                  │                            │
│  ┌───────▼────────┐ ┌───────▼────────┐ ┌───────▼─────────┐ ┌─────────────┐  │
│  │ HostManager    │ │ IpcServer      │ │ StaticFallback  │ │ VramBudget  │  │
│  │ Job/Object     │ │ framing/RPC    │ │ ID壁纸 COM      │ │ DXGI query  │  │
│  └───────┬────────┘ └───────┬────────┘ └─────────────────┘ └─────────────┘  │
└──────────┼──────────────────┼───────────────────────────────────────────────┘
           │ CreateProcess    │ stdin/stdout 匿名管道
           │ + Job Object     │ 4B length + MessagePack
           ▼                  ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│ Wallerpaper.RenderHost.exe  （每个壁纸实例 × 显示器一个）                    │
│                                                                              │
│  ┌───────────────┐  ┌────────────────┐  ┌────────────────────────────────┐  │
│  │ HostMain      │  │ HostWindow     │  │ IpcClient                      │  │
│  │ DPI/COM/Job   │  │ Win32/WinForms │  │ reader/writer/heartbeat        │  │
│  └───────┬───────┘  └───────┬────────┘  └───────────────┬────────────────┘  │
│          │                  │                           │                   │
│  ┌───────▼──────────────────▼───────────────────────────▼────────────────┐  │
│  │ IWallpaperRenderer                                                    │  │
│  │  VideoRenderer(libmpv) │ WebRenderer(WebView2) │ SceneRenderer(D3D11) │  │
│  └───────┬──────────────────┬──────────────────────────┬──────────────────┘  │
│          │                  │                          │                     │
│  ┌───────▼───────┐  ┌───────▼────────────┐  ┌──────────▼─────────────────┐  │
│  │ libmpv-2.dll  │  │ msedgewebview2.exe │  │ D3D11 device + swapchain   │  │
│  │ （进程内）     │  │ 浏览器/GPU/渲染树   │  │ D3DCompile + WIC           │  │
│  └───────────────┘  └────────────────────┘  └────────────────────────────┘  │
└──────────┬───────────────────────────────────────────────────────────────────┘
           │ SetParent / SetWindowPos / ShowWindow
           ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│ explorer.exe（当前会话）                                                     │
│  [经典] Progman + 顶层 WorkerW（DefView 所在 WorkerW 的兄弟）── 壁纸 child  │
│  [raised] Progman ── SHELLDLL_DefView + layered 壁纸 child（DefView 下方）   │
└──────────────────────────────────────────────────────────────────────────────┘
```

信任边界：

- 主进程：完全可信，持有 Job Object、配置、库索引、静态壁纸 COM。
- RenderHost：**半可信**。只接受枚举化命令与库根目录内路径；不接收任意命令行/脚本；不暴露 host object 给网页。
- WebView2 内容：**不可信**。Chromium 沙箱 + 关闭 host object/下载/弹窗/权限；本地内容用虚拟主机映射，不用 `file://`。
- 壁纸包：**不可信输入**。Zip Slip/Zip Bomb/可执行文件/路径穿越全部拒绝。
- Explorer：外部进程。只做窗口挂靠与消息监听，不注入、不 hook、不调试。

### 1.3 模块清单

| 模块 | 职责 | 进程 | 通信 | 关键 API/依赖 |
|---|---|---|---|---|
| `Shell.Views` | 库、设置、显示器布局、诊断、对话框 | App | 进程内 MVVM | WinUI 3, CommunityToolkit.Mvvm |
| `Shell.ViewModels` | UI 状态与命令，不碰 HWND | App | 进程内 | — |
| `TrayService` | 托盘图标/菜单，Explorer 重启后重加 | App | `Shell_NotifyIcon` 或 H.NotifyIcon.WinUI | `NOTIFYICONDATA`, `TaskbarCreated` |
| `HotkeyService` | 全局热键注册/冲突提示 | App | `WM_HOTKEY` | `RegisterHotKey` |
| `SingleInstanceService` | 单实例 + 二次启动激活 | App | 命名 Mutex + 隐藏窗口 + `WM_COPYDATA` | `CreateMutex`, `FindWindow`, `SendMessageTimeout` |
| `MessageWindow` | 系统消息汇聚：TaskbarCreated、DisplayChange、DPICHANGED、Power、Session、CopyData；必须是**隐藏的 top-level 窗口**（不能用 `HWND_MESSAGE`，否则收不到广播） | App | Win32 消息 | `CreateWindowEx`, `WndProc`, `RegisterWindowMessage` |
| `LibraryService` | 导入、校验、元数据、缩略图、搜索/标签、文件监听 | App | 进程内 + 文件系统 | `ZipArchive`, `FileSystemWatcher`, WIC |
| `MonitorService` | 枚举/稳定 ID/工作区/DPI/热插拔 | App | 进程内 + Win32 消息 | `EnumDisplayMonitors`, `GetMonitorInfo`, `DisplayConfigGetDeviceInfo` |
| `DesktopService` | WorkerW/Progman/DefView 发现、raised desktop 判定、挂靠策略、z-order、Explorer 监听 | App | 进程内 + Win32 | `FindWindowEx`, `SendMessageTimeout`, `SetWinEventHook` |
| `WallpaperOrchestrator` | 期望状态 vs 实际状态；按显示器分配；启动/停止/重启 | App | 进程内 | 状态机，`Channel<T>` |
| `RenderHostManager` | CreateProcess、Job、stdio、环境、重启/退避/崩溃预算 | App | 进程创建 + 管道 | `CreateJobObject`, `SetInformationJobObject`, `Process` |
| `IpcServer` | 每 Host 一个通道；帧解析、RPC、心跳、背压 | App | 匿名管道 | `System.IO.Pipelines`, MessagePack |
| `PlaybackPolicy` | 全屏/锁屏/RDP/电源/电池/屏保/遮挡 → 暂停原因集合 | App | 进程内 | `SHQueryUserNotificationState`, WTS, Power |
| `FullscreenWatcher` | 独占全屏 + 无边框全屏 + 每显示器覆盖 | App | 进程内 | `GetForegroundWindow`, `GetWindowRect`, `DwmGetWindowAttribute` |
| `SessionPowerWatcher` | 锁屏/RDP/显示器关闭/电池节省 | App | Win32 消息 | `WTSRegisterSessionNotification`, `RegisterPowerSettingNotification` |
| `VramBudgetService` | 适配器预算/用量、压力判定、卸载/终止决策 | App | DXGI + IPC 统计 | `IDXGIAdapter3::QueryVideoMemoryInfo` |
| `StaticFallbackService` | 每显示器静态快照、原壁纸保存/恢复 | App | COM | `IDesktopWallpaper` |
| `InputService` | RawInput 鼠标（键盘仅 Web 交互模式显式开启）、光标广播、输入转发策略 | App | Win32 + IPC | `RegisterRawInputDevices`, `GetRawInputData` |
| `PreviewService` | 库内预览：视频 `MediaPlayerElement`（不支持则缩略图）、Web 缩略图/短预览（独立 profile、短生命周期） | App | 进程内 | WinUI MediaPlayerElement, WebView2 |
| `SettingsService` | JSON 配置、原子写、schema 迁移 | App | 文件 | `System.Text.Json` |
| `Diagnostics` | 日志、指标、崩溃转储、支持包、调试 overlay | App + Host | 文件/ETW | Serilog/NLog, `MiniDumpWriteDump` |
| `UpdateService` | 检查/下载/应用更新、回滚 | App | HTTPS | Velopack |
| `Contracts` | DTO、命令/事件、错误码、协议版本 | 共享 | — | MessagePack 注解 |
| `Interop` | P/Invoke、SafeHandle、结构体、COM 声明 | 共享 | — | `LibraryImport`/`DllImport` |
| `HostMain` | 参数、DPI、COM、Job、stdio、消息循环、生命周期 | Host | 管道 | `SetProcessDpiAwarenessContext`, `CoInitializeEx` |
| `HostWindow` | 窗口创建、样式、挂靠/解挂、Resize、DPI、窗口消息 | Host | Win32 | `CreateWindowEx`, `SetParent`, `SetWindowPos`, `MapWindowPoints` |
| `IpcClient` | 帧读写、RPC 分发、心跳、取消 | Host | 匿名管道 | `System.IO.Pipelines` |
| `VideoRenderer` | libmpv 初始化/命令/属性/事件/音频/截图 | Host | libmpv C API | `mpv_create`, `mpv_set_option_string`, `mpv_command` |
| `WebRenderer` | WebView2 环境/控制器/安全设置/挂起/截图/JS 桥 | Host | WebView2 .NET | `CoreWebView2Environment`, `CoreWebView2Controller` |
| `SceneRenderer` | D3D11 设备/交换链/HLSL/精灵/ uniforms/帧调度 | Host | D3D11 | `D3D11CreateDevice`, `CreateSwapChainForHwnd`, `D3DCompile` |
| `HostTelemetry` | FPS/丢帧/显存/CPU/内存/DeviceLost | Host | IPC | DXGI, `Process` |
| `CrashGuard` | 未处理异常、minidump、退出码 | Host | 文件 | `MiniDumpWriteDump` |

通信矩阵：

| 通信对 | 方式 | 方向 | 内容 |
|---|---|---|---|
| UI ↔ Core | 进程内接口/`Channel<T>` | 双向 | 用户命令、状态快照、错误 |
| Core ↔ RenderHost | stdin/stdout 匿名管道，长度前缀 MessagePack | 双向 | 命令/事件/统计/心跳；不传帧 |
| Core ↔ Explorer | Win32 消息 + `SetParent`/`SetWindowPos` | 双向（消息）/单向（窗口操作） | `TaskbarCreated`、WorkerW/DefView 句柄、挂靠 |
| Core ↔ OS | 隐藏 top-level 窗口消息 | 单向通知 | Display/Power/Session/Hotkey/CopyData |
| Core ↔ 静态壁纸 | COM `IDesktopWallpaper` | 双向 | 每显示器快照/原壁纸 |
| RenderHost ↔ GPU | D3D11/DXGI | — | 设备、交换链、纹理、查询 |
| RenderHost ↔ libmpv | C API（同进程） | 双向 | 命令/属性/事件 |
| RenderHost ↔ WebView2 | .NET API + Chromium 进程树 | 双向 | 导航/设置/挂起/截图；页面输入走 JS 桥 |
| UI ↔ Preview | 进程内 `MediaPlayerElement`/WebView2 | 双向 | 预览；不复用壁纸 Host |
| Core ↔ 子进程树 | Job Object/进程句柄 | 单向控制 | 生命周期、终止、崩溃清理 |

### 1.4 解决方案/项目结构

```text
Wallerpaper.sln
├─ src/
│  ├─ Wallerpaper.App/                 # WinUI 3, net10.0-windows10.0.19041.0
│  │  ├─ App.xaml / App.xaml.cs
│  │  ├─ Views/ ViewModels/ Controls/
│  │  ├─ Services/                     # 薄适配层，调用 Core
│  │  └─ app.manifest                  # PerMonitorV2, longPathAware
│  ├─ Wallerpaper.Core/                # net10.0，无 UI 依赖
│  │  ├─ Library/ Displays/ Desktop/ Playback/
│  │  ├─ Hosting/ Ipc/ Input/ Diagnostics/
│  │  └─ StaticFallback/ Settings/
│  ├─ Wallerpaper.Contracts/           # netstandard2.0/net10.0
│  │  ├─ Dto/ Commands/ Events/ Enums/ Errors/
│  │  └─ ProtocolVersion.cs
│  ├─ Wallerpaper.Interop/             # net10.0-windows
│  │  ├─ Win32/ User32.cs Kernel32.cs Shell32.cs Dwmapi.cs
│  │  ├─ Display/ Dpi/ Power/ Session/ Job/
│  │  └─ Com/ IDesktopWallpaper.cs
│  ├─ Wallerpaper.RenderHost/           # net10.0-windows, WinExe/Console
│  │  ├─ Program.cs HostWindow.cs
│  │  ├─ Ipc/ Renderers/ Telemetry/ Crash/
│  │  └─ app.manifest
│  ├─ Wallerpaper.Render.Video/         # libmpv P/Invoke + 封装
│  ├─ Wallerpaper.Render.Web/           # WebView2 封装
│  ├─ Wallerpaper.Render.Scene/         # D3D11/场景格式/着色器
│  └─ Wallerpaper.Tools/                # 打包、校验、缩略图 CLI
├─ tests/
│  ├─ Wallerpaper.UnitTests/
│  ├─ Wallerpaper.IntegrationTests/
│  ├─ Wallerpaper.SceneTests/
│  └─ Wallerpaper.UiTests/              # FlaUI
├─ docs/
│  ├─ ARCHITECTURE.md
│  ├─ IPC-PROTOCOL.md
│  ├─ SCENE-FORMAT.md
│  ├─ COMPATIBILITY.md
│  └─ adr/
├─ assets/
├─ Directory.Build.props
├─ Directory.Packages.props
└─ global.json
```

关键 csproj 属性（主进程）：

```xml
<TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
<UseWinUI>true</UseWinUI>
<WindowsPackageType>None</WindowsPackageType>
<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>
<SelfContained>true</SelfContained>
<RuntimeIdentifier>win-x64</RuntimeIdentifier>
<ApplicationManifest>app.manifest</ApplicationManifest>
<Nullable>enable</Nullable>
<ImplicitUsings>enable</ImplicitUsings>
```

> WinUI 3 非打包需要 Windows App SDK；优先 `WindowsAppSDKSelfContained=true` 自包含，若目标 OS/工具链有限制则回退安装器部署 Windows App Runtime。仍需检测 WebView2 Runtime；如果 libmpv 的原生构建依赖 VC++，安装器再检测/安装 VC++。发布不要开 `PublishTrimmed`（WinUI 3/WebView2 与反射在裁剪下风险很高），也不要开 `PublishSingleFile`（会打散 libmpv/WebView2 依赖的原生目录结构）。

### 1.5 主进程内部模块关系

```text
WinUI Shell ──► Application Core Facade ──► WallpaperOrchestrator
                    │                          │
                    │                          ├─ MonitorService
                    │                          ├─ DesktopService ──► Explorer
                    │                          ├─ PlaybackPolicy ◄─ FullscreenWatcher
                    │                          │                  ◄─ SessionPowerWatcher
                    │                          ├─ RenderHostManager ──► RenderHost[]
                    │                          ├─ IpcServer ◄──────────┘
                    │                          ├─ StaticFallbackService ──► IDesktopWallpaper
                    │                          └─ VramBudgetService
                    ├─ LibraryService
                    ├─ SettingsService
                    ├─ InputService
                    ├─ PreviewService
                    └─ Diagnostics
```

规则：

- UI 线程只做 UI；编排在专用线程（`Channel<OrchestratorCommand>` 消费者），避免 UI 卡顿导致暂停/恢复延迟。
- `DesktopService` 的所有 Win32 调用在后台线程；结果通过不可变快照交给编排器。
- `MessageWindow` 的 `WndProc` 只做“记录事实 + Post 到编排队列”，不做阻塞调用（尤其不在这里 `SetParent`）。
- 任何 `Process.WaitForExit`/管道读都不允许在 UI 线程同步等待。
- “关闭主窗口”= 取消 `AppWindow.Closing` 并 `AppWindow.Hide()`，不要 `Close()` 销毁最后一个 XAML 窗口；隐藏的 top-level message window 保留消息循环。真正退出只走托盘“退出”。

### 1.6 RenderHost 内部结构

```text
Program.Main
 ├─ SetProcessDpiAwarenessContext(PerMonitorV2)
 ├─ CoInitializeEx(STA) / Application.SetHighDpiMode(PerMonitorV2)
 ├─ 解析最小参数（--instance, --type, --parent-pid）
 ├─ 建立 stdio IpcClient（Hello/Ready）
 ├─ Application.Run(HostForm)  ── 消息循环 + SynchronizationContext
 │    ├─ HostWindow（Form，borderless/toolwindow/noactivate）
 │    ├─ RendererFactory.Create(type)
 │    │    ├─ VideoRenderer   （libmpv 自己的渲染线程）
 │    │    ├─ WebRenderer     （WebView2 控制器，UI 线程）
 │    │    └─ SceneRenderer   （独立渲染线程 + D3D11）
 │    ├─ FrameScheduler / PauseGate
 │    └─ Telemetry timer（1s）
 └─ Shutdown: renderer.Dispose → mpv_terminate_destroy/WebView2.Close → 退出
```

线程模型：

| 线程 | 归属 | 职责 | 禁止 |
|---|---|---|---|
| UI/消息线程 | Host | 窗口消息、WebView2 控制器、挂靠/Resize | 长耗时 GPU 初始化、阻塞 IPC |
| 渲染线程 | Scene | D3D11 设备/交换链/绘制/Present | 直接碰 WinForms 控件（除 HWND 值） |
| mpv 事件线程 | Video | `mpv_wait_event`、状态上报 | 调 UI |
| IPC 读线程 | Host | 解帧、分发命令、心跳 | 直接重建渲染器；投递到 UI/渲染队列 |
| 遥测定时器 | Host | 采集 FPS/显存/内存 | 阻塞渲染 |

### 1.7 壁纸实例状态机

```text
                 ┌──────────┐
                 │ Created  │
                 └────┬─────┘
                      │ Start
                 ┌────▼─────┐
                 │ Starting │──失败──► Failed ──► Restarting（退避）
                 └────┬─────┘
                      │ Hello/Ready
                 ┌────▼─────┐
                 │  Ready   │
                 └────┬─────┘
                      │ Load
                 ┌────▼─────┐
                 │ Loading  │──失败──► Failed
                 └────┬─────┘
                      │ Loaded + FirstFrame
                 ┌────▼─────┐
                 │ Attached │◄──────────────┐
                 └────┬─────┘               │ Reattach
                      │ Play                │
                 ┌────▼─────┐   Pause   ┌───┴────┐
                 │ Playing  │◄─────────►│ Paused │
                 └────┬─────┘           └───┬────┘
                      │                     │ Unload
                      │                ┌────▼─────┐
                      │                │ Unloaded │
                      │                └────┬─────┘
                      │                     │ Terminate
                      │                ┌────▼─────┐
                      └───────────────►│ Stopped  │
                                       └──────────┘
```

暂停不是布尔值，而是**原因集合**：

```csharp
[Flags]
enum PauseReason
{
    None = 0,
    User = 1 << 0,
    Fullscreen = 1 << 1,
    SessionLocked = 1 << 2,
    RemoteSession = 1 << 3,
    DisplayOff = 1 << 4,
    BatterySaver = 1 << 5,
    Occluded = 1 << 6,
    Screensaver = 1 << 7,
    ExplorerRestart = 1 << 8,
    Error = 1 << 9,
}
```

`Paused` 当且仅当 `reasons != 0`。恢复时必须所有原因清除；避免“游戏退出但锁屏还在”时误恢复。

释放级别：

```csharp
enum ReleaseLevel
{
    None,        // 正常播放
    RenderOnly,  // 停止 Present，保留解码/资源
    Media,       // 暂停解码/媒体，保留进程与窗口
    Unload,      // 释放解码器/GPU 资源，保留进程
    Terminated   // 杀 Host，静态快照兜底
}
```

升级策略（默认，可配置）：

| 条件 | 级别 | 动作 |
|---|---|---|
| 刚进入暂停 | `RenderOnly` | 停止 Present；Web `IsVisible=false` + `TrySuspendAsync`；视频 `pause=yes` |
| 暂停 ≥ 15s 或显存压力 | `Media` | 视频 `stop`/`vid=no`；Web 关闭媒体；场景停渲染并释放 RT |
| 暂停 ≥ 60s（全屏/显存压力） | `Terminated` | 先设静态快照，再杀 Host；恢复时冷启动 |
| 用户手动暂停 | 5min 后 `Unload`，不自动杀 | 保留快速恢复 |
| 显存超预算 | 立即降级最不重要的实例 | 优先杀 4K/后台实例 |

### 1.8 IPC 协议

#### 1.8.1 传输

- 每个 Host 一个**匿名管道对**：父 → 子 stdin，子 → 父 stdout。
- 子进程 stderr 单独作为**文本日志流**，父进程必须持续 drain，避免子进程写满阻塞。
- 不使用 localhost TCP、不注册全局命名管道；避免端口冲突、ACL 配置、命名抢占。
- 命名管道仅保留给 `--debug-ipc` 或未来“带外诊断”，默认关闭。

优点：无名称、无 ACL、无网络面；父进程死 → 子进程 stdin EOF → 自杀；Job Object 再兜底。

#### 1.8.2 帧格式

```text
+--------+--------+--------+--------+----------------------+
| length (4B, LE) | ver(1B)| type(1B)| requestId (8B)      |
+--------+--------+--------+--------+----------------------+
| MessagePack payload (length - 10 bytes)                  |
+----------------------------------------------------------+
```

- `length` 最大 1 MiB；超过直接断链并记录。
- `ver` = 协议主版本；Host/App 必须一致；不一致 → 拒绝启动并提示更新。
- `type` = `Command` / `Event` / `Response` / `Heartbeat`。
- `requestId` 用于请求/响应关联；事件为 0。
- MessagePack 使用显式整型 key，字段只追加不重排；禁止 contractless。

#### 1.8.3 握手

```text
Parent → Child: Hello { protocolVersion, appVersion, instanceId, hostType, parentPid, logLevel }
Child  → Parent: Ready { protocolVersion, hostVersion, capabilities[], osBuild, gpuName, sessionId }
Parent → Child: Load { spec }        // 只传库内路径/URL/场景清单路径，不传大二进制
Child  → Parent: Loaded { firstFrameReady, actualSize, hwnd }
Parent → Child: Attach { workerW, defView, mode, monitorRect, dpi, zOrder }
Child  → Parent: Attached { hwnd, actualRect }
Parent → Child: Play
```

超时：握手 15s（WebView2 冷启动可能慢）；Load 30s（4K/网络）；普通命令 5s；心跳 2s，6s 无心跳判死。

#### 1.8.4 命令/事件目录（v1）

| 方向 | 名称 | 关键字段 | 说明 |
|---|---|---|---|
| P→C | `Load` | `specPath`, `type`, `fit`, `muted`, `volume`, `startAt` | 只传路径；Host 校验在 library root 内 |
| P→C | `Attach` | `workerW`, `defView`, `mode`, `rect`, `dpi`, `zOrder` | 模式：Classic / RaisedLayered / ProgmanFallback |
| P→C | `Detach` | — | 解挂、隐藏、等待重挂 |
| P→C | `Play` / `Pause` | `reasons`, `releaseLevel` | 原因集合 + 释放级别 |
| P→C | `Resize` | `rect`, `dpi` | 显示器分辨率/DPI 变化 |
| P→C | `SetVolume` / `SetMute` | `value` | 音频策略由主进程决定 |
| P→C | `SetCursor` | `x`, `y`, `inside` | 30Hz 合并；只给需要鼠标效果的渲染器 |
| P→C | `CaptureSnapshot` | `path`, `format` | 静态兜底/缩略图 |
| P→C | `Shutdown` | `reason` | 优雅退出 |
| C→P | `Ready` / `Loaded` / `Attached` | 见握手 | — |
| C→P | `FirstFrame` | `timestamp` | 用于启动性能统计 |
| C→P | `StateChanged` | `state`, `reasons` | 实际状态 |
| C→P | `Stats` | `fps`, `dropped`, `vram`, `cpu`, `ws`, `deviceLost` | 1Hz |
| C→P | `Error` | `code`, `message`, `detail` | 结构化错误 |
| C→P | `WindowLost` | `hwnd` | WorkerW 销毁/窗口被毁 |
| C→P | `DeviceLost` | `hr`, `adapter` | `DXGI_ERROR_DEVICE_REMOVED/RESET` |
| C→P | `Log` | `level`, `message` | 可选；默认日志走 stderr |
| C↔P | `Ping` / `Pong` | `ts` | 心跳 |

#### 1.8.5 错误码（节选）

| 码 | 含义 | 主进程动作 |
|---|---|---|
| `E_PROTOCOL` | 帧/版本错误 | 杀 Host，提示更新 |
| `E_BAD_SPEC` | 路径越界/格式不支持 | 不重启，标记壁纸不可用 |
| `E_MPV_INIT` | libmpv 初始化失败 | 重试 1 次，仍失败则禁用 |
| `E_WEBVIEW_RUNTIME` | WebView2 Runtime 缺失 | 提示安装，不重启 |
| `E_WEBVIEW_CRASH` | 浏览器/渲染进程崩溃 | 重启 Host（计入崩溃预算） |
| `E_D3D_DEVICE` | 设备丢失/TDR | 重建一次；再失败禁用 |
| `E_SHADER` | HLSL 编译/验证失败 | 不重启，显示错误壁纸 |
| `E_ATTACH` | SetParent/定位失败 | 切换挂靠模式/Progman 兜底 |
| `E_OOM` | 显存/内存不足 | 降级/杀实例 |

#### 1.8.6 安全

- stdio 句柄只由父进程继承；无命名对象可被同机其他进程连接。
- 子进程只接受枚举命令；路径必须 `Path.GetFullPath` 后位于 library root 前缀内，拒绝 `..`、ADS、设备名、UNC（除非显式允许）。
- 父进程用 `ArgumentList` 而不是拼接命令行；不经过 shell。
- 子进程环境清空敏感变量；`WEBVIEW2_USER_DATA_FOLDER`、`MPV_HOME` 显式指定。
- 任何 IPC 字段都做长度/范围校验；拒绝未知 type；未知字段忽略（向前兼容）。

### 1.9 桌面挂靠（WorkerW / Progman）

#### 1.9.1 两条路径

| 路径 | 判定 | 父窗口 | 窗口样式 | z-order |
|---|---|---|---|---|
| 经典 WorkerW | `Progman` 无 `WS_EX_NOREDIRECTIONBITMAP`，存在含 `SHELLDLL_DefView` 的 WorkerW | DefView 后面的那个 WorkerW | 挂靠前清除 `WS_POPUP`、设置 `WS_CHILD`；`WS_EX_NOACTIVATE`（`WS_EX_TOOLWINDOW` 可保留，child 本身不进 Alt+Tab） | 先按屏幕 z-order 放到底，再挂到 WorkerW；不与其他 child 竞争 |
| Win11 raised desktop | `Progman` 有 `WS_EX_NOREDIRECTIONBITMAP`（主判定）；`SHELLDLL_DefView` 有 `WS_EX_LAYERED` 仅作辅助确认，不单独切换模式 | **Progman** | `WS_CHILD` + `WS_EX_LAYERED`（`SetLayeredWindowAttributes` alpha=255） | 在 `SHELLDLL_DefView` 正下方；若有 `WorkerW` 则压到底部，没有则不碰 0 句柄 |

经典路径的发现算法（对应 Lively `WinDesktopCore.SetupDesktopLayer()`）：

```text
progman = FindWindow("Progman", null)
if progman == 0 -> 等待/重试（Explorer 未就绪）

// 让 Explorer 在 DefView 后面生成 WorkerW；未文档化消息
SendMessageTimeout(progman, 0x052C, 0xD, 0x1, SMTO_NORMAL, 1000, out _)

EnumWindows:
  if FindWindowEx(top, 0, "SHELLDLL_DefView", 0) != 0:
      defView = that
      workerW = FindWindowEx(0, top, "WorkerW", 0)   // top 之后的兄弟
      // 可能有多个 WorkerW；校验 class/rect/可见性后选一个

if workerW == 0:
  // 新 Win11：DefView 可能直接在 Progman 下；尝试 Progman 下的 WorkerW
  workerW = FindWindowEx(progman, 0, "WorkerW", 0)

if 仍失败:
  记录能力探测失败；使用静态壁纸兜底；不覆盖图标
```

关键点：

- `0x052C` 是 **未文档化** 的 Progman 消息，Lively 用 `wParam=0xD, lParam=0x1`；很多旧代码用 `0,0`。我们按“Lively 参数 → `0,0` → 已有 WorkerW”顺序尝试，不把它当稳定 API。
- `FindWindowEx(0, top, "WorkerW", 0)` 的第二个参数是“从哪个窗口之后找”，用来拿 Top 后面的那个 WorkerW；必须校验它确实覆盖目标显示器/虚拟桌面，不能盲信第一个。
- `Progman` 可能已经是 DefView 的父窗口；某些 Win11 版本没有独立 WorkerW，或 WorkerW 的层级不同。此时走 raised/Progman 路径。
- raised 模式下 `WorkerW` 可能不存在或句柄为 0；`SetWindowPos(HWND.NULL, ...)` 是无效调用，必须有守卫。没有 WorkerW 时只做 DefView 下方的 z-order，不强行“压底”。
- 多屏时先检查 `GetWindowRect(workerW)` 是否覆盖目标显示器；不覆盖就尝试 Progman/该屏可用的 WorkerW/静态兜底。子窗口被定位到父客户区外会被裁剪，表现为“某块屏黑”。

#### 1.9.2 挂靠时序

```text
App.Core                 RenderHost                    Explorer
   │                         │                            │
   │ DiscoverWorkerW()       │                            │
   │────────────────────────►│                            │
   │ Attach{mode,rect,dpi}   │                            │
   │────────────────────────►│                            │
   │                         │ 1. 创建/准备窗口            │
   │                         │ 2. 清 WS_POPUP / 设 WS_CHILD│
   │                         │    （raised 再加 LAYERED）   │
   │                         │ 3. SetParent(parent)        │
   │                         │ 4. MapWindowPoints/SetWindowPos
   │                         │    （SWP_FRAMECHANGED 应用样式）
   │                         │ 5. ShowWindow(SW_SHOWNOACTIVATE)
   │                         │ 6. 校验 z-order（必要时调整）│
   │                         │───────────────────────────►│
   │                         │◄───────────────────────────│
   │ Attached{actualRect}    │                            │
   │◄────────────────────────│                            │
   │ 校验 GetWindowRect/GetClientRect 与 monitorRect 一致 │
   │ 不一致 → 修正一次 → 仍失败 → 切换模式/兜底           │
```

坐标要点：

- 挂靠前窗口是 top-level，用**屏幕物理像素**定位；挂靠后子窗口坐标相对父窗口客户区。
- 用 `MapWindowPoints(HWND_DESKTOP, parent, &pt, 1)` 把屏幕左上角转换到父客户区，再 `SetWindowPos`。Lively 用 `MapWindowPoints(handle, workerW, ...)` 的等价做法。
- 每屏壁纸：`x/y = monitor.rcMonitor`；`w/h = rcMonitor`。`span`：`0,0,workerW.rect.w/h`（仅经典路径；raised 模式另算）。
- 挂靠后必须校验 `GetWindowRect`（屏幕坐标）与 `rcMonitor` 的误差 ≤1px；`GetClientRect` 的宽高等于显示器物理像素。DPI 虚拟化会让这个校验失败，见 1.10。

挂靠伪代码（Host 进程内）：

```csharp
// parent / insertAfter 由主进程根据模式给出
// classic: parent = workerW, insertAfter = HWND_BOTTOM
// raised:  parent = progman,  insertAfter = shellDLL_DefView

var style = GetWindowLongPtr(hwnd, GWL_STYLE);
SetWindowLongPtr(hwnd, GWL_STYLE, (style & ~WS_POPUP) | WS_CHILD);
SetWindowLongPtr(hwnd, GWL_EXSTYLE,
    GetWindowLongPtr(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

if (raised)
{
    SetWindowLongPtr(hwnd, GWL_EXSTYLE,
        GetWindowLongPtr(hwnd, GWL_EXSTYLE) | WS_EX_LAYERED);
    SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA); // 必须在 SetParent 前
}

var pt = new POINT { X = rc.Left, Y = rc.Top };
MapWindowPoints(HWND_DESKTOP, parent, ref pt, 1); // 屏幕 -> 父客户区

SetParent(hwnd, parent);

SetWindowPos(hwnd, insertAfter, pt.X, pt.Y,
    rc.Right - rc.Left, rc.Bottom - rc.Top,
    SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
ShowWindow(hwnd, SW_SHOWNOACTIVATE); // 双保险；child 不会激活
```

> 注意：`SetParent` 的样式切换顺序、`SWP_FRAMECHANGED`、`WS_EX_LAYERED` 的时机在不同驱动/窗口类型上可能有差异；Phase 0 用实际 Video/Web/Scene 三种窗口各验证一遍。

#### 1.9.3 图标点击穿透

正确做法是 **z-order**：

- 经典路径：壁纸是“图标 WorkerW”后面的另一个 WorkerW 的 child；`SHELLDLL_DefView` 在整个层级之上，单击/双击/右键/框选都命中 DefView，壁纸收不到鼠标。
- raised 路径：壁纸是 Progman 的 child，`SetWindowPos(hwnd, shellDLL_DefView, ...)` 把它放在 DefView 正下方；DefView 是 layered 的透明层，但命中测试仍优先。
- 因此**默认不要**依赖 `WS_EX_TRANSPARENT`。它只影响命中测试，不会自动实现图标穿透；滥用还会让壁纸无法接收真实鼠标、增加 DWM 行为的不确定性。
- 不要用 `SetWindowRgn` 挖图标洞；图标会移动。不要隐藏/禁用 `SHELLDLL_DefView`。不要改 Explorer 的图标可见性（除非用户显式要求）。

验收：单击图标选中、双击打开、右键菜单、桌面框选、F5 刷新、键盘 type-ahead 全部正常；壁纸永不抢焦点；Alt+Tab 不出现；任务栏无按钮。

#### 1.9.4 Explorer 重启

触发：

- `RegisterWindowMessage("TaskbarCreated")` 广播（Lively `WndProcMsgWindow` 的做法）。
- `SetWinEventHook(EVENT_OBJECT_DESTROY, ..., WINEVENT_OUTOFCONTEXT)` 监听 WorkerW 线程（Lively `WorkerWHook_EventReceived` 的做法）；必须在带消息循环的线程上调用/回收。
- `SystemEvents.SessionSwitch` / `WM_WTSSESSION_CHANGE` 解锁后校验 `IsWindow(workerW)`（Lively `SystemEvents_SessionSwitch` 的做法）。

`TaskbarCreated` 与 `EVENT_OBJECT_DESTROY` 可能同时/先后到达；用 Explorer generation + 500ms 时间窗去重，避免双重重启。

处理：

1. `TaskbarCreated` 到达后比较 `Shell_TrayWnd` 的 Explorer PID；PID 没变说明只是 DPI/显示变化导致的广播，忽略。
2. Host 侧捕获自己窗口的 `WM_NCDESTROY`（或定时 `IsWindow` 校验），上报 `WindowLost`；主进程据此确认 WorkerW 销毁，而不是只等广播。
3. 标记 `ExplorerRestart` 暂停原因；保留静态快照；停止 Present。
4. 等待新 `Progman`/`WorkerW` 就绪（重试 20×100ms）。
5. v1：**重启所有 Host**（视频/网页/场景都重启），最简单可靠；静态快照覆盖空窗。
6. 重新挂靠；重新添加托盘图标；校验窗口矩形；失败则指数退避重试。
7. 60s 内 Explorer 重启 >3 次：停止自动恢复，托盘通知，保留静态壁纸，等用户手动“修复桌面”。

> 为什么 v1 不尝试“无重启重挂”：WorkerW 销毁会连带销毁其 child HWND；libmpv 的 `wid` 不能换窗口；WebView2 控制器也绑定原 HWND。重挂需要重建窗口/重建渲染器，复杂度接近重启。先用“静态快照 + 快速重启”保证体验，Phase 4 再优化 Web/Scene 的快速重挂。

### 1.10 多显示器与高 DPI

#### 1.10.1 坐标模型

- 全项目内部统一使用 **物理像素** 和 **虚拟屏幕坐标**（`rcMonitor`/`rcWork` 来自 `GetMonitorInfo`，在 PerMonitorV2 进程里是物理像素）。
- UI 的 DIP 只在 WinUI 层使用；传给 Core/RenderHost 之前转成物理像素。
- 子窗口位置 = 父客户区坐标；跨进程父窗口用 `MapWindowPoints(HWND_DESKTOP, parent, ...)`。
- 不使用 WinUI `DisplayArea.WorkArea` 作为子窗口几何的唯一来源；它适合 UI 布局，但壁纸几何以 Win32 `GetMonitorInfo` 为准。

#### 1.10.2 DPI 感知

- 主进程与 RenderHost 的 `app.manifest` 都声明 `PerMonitorV2`；`Program.Main` 最早期再调用 `SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)` 兜底。
- RenderHost 若用 WinForms，`Application.SetHighDpiMode(HighDpiMode.PerMonitorV2)` 必须在创建任何窗口前调用。
- 跨进程挂靠时，Explorer 的 DPI 感知可能不同；在 `SetParent`/`MapWindowPoints` 前后用 `SetThreadDpiAwarenessContext(PER_MONITOR_AWARE_V2)`，必要时用 `SetThreadDpiHostingBehavior(DPI_HOSTING_BEHAVIOR_MIXED)`（Win10 1803+）做混合宿主实验。
- 取显示器 DPI 的可靠顺序：
  1. 在目标显示器上创建一个 1×1 的隐藏 top-level 窗口，`GetDpiForWindow`；
  2. 或对已定位到该显示器的 Host 窗口调用 `GetDpiForWindow`；
  3. 最后才用 `GetDpiForMonitor(MDT_EFFECTIVE_DPI)`（MSDN 明确说 Per-Monitor 感知线程应优先 `GetDpiForWindow`）。
- 不要用 `GetDpiForMonitor` 的结果直接乘 WinUI 的 DIP；两套坐标混用是 1px 缝隙/错位的主要来源。

#### 1.10.3 WebView2 的 DPI

- `CoreWebView2Controller.RasterizationScale` 是“显示器 DPI 缩放 × 文本缩放”的组合；`ShouldDetectMonitorScaleChanges=false` 后由我们显式设置。
- `Bounds` 使用 DIP；物理像素 = `Bounds * RasterizationScale`。Resize 时先更新 `Bounds`，再更新 `RasterizationScale`，顺序和时机要测。
- 文本缩放（Windows 辅助功能“文本大小”）要纳入 `RasterizationScale`；可通过 `Windows.UI.ViewManagement.UISettings.TextScaleFactor` 读取（WinRT），不要只算 `dpi/96`。
- 混合 DPI 下切换显示器：先改 `Bounds`/scale，再 `SetWindowPos`；避免 WebView2 先按旧 scale 重排导致闪烁。

#### 1.10.4 多屏策略

| 模式 | 进程/窗口 | 说明 |
|---|---|---|
| `per`（默认） | 每显示器一个 Host | 独立壁纸、独立暂停、独立崩溃域；推荐 |
| `duplicate` | 每显示器一个 Host，同一壁纸 | 同步播放位置；只有主/选中屏出声；显存翻倍 |
| `span` | 一个 Host 跨虚拟屏 | 只建议同 DPI/同刷新率；混合 DPI 下内容会被拉伸/模糊；1.0 不做 |

热插拔：

- `WM_DISPLAYCHANGE` 是主信号；`WM_SETTINGCHANGE` + `SPI_SETWORKAREA` 补充工作区变化；`RegisterDeviceNotification(GUID_DEVINTERFACE_MONITOR)` 补充显示器到达/移除。
- 用 `DisplayConfigGetDeviceInfo` 的 `monitorDevicePath` + EDID 厂商/产品码 + `connectorInstance` 组成稳定 ID；`\\.\DISPLAY1` 只作为会话内名称，不作为持久键。
- 显示器断开：保留分配配置，停止该 Host；重新连接：按稳定 ID 恢复。Lively 的 `wallpapersDisconnected` 是同样思路。
- 主屏切换/排列变化：重新计算所有 `rcMonitor`，逐个 `Resize`；不要重建整个 Host。

### 1.11 生命周期、暂停与显存

#### 1.11.1 暂停判定

按优先级：

1. 系统状态：锁屏、RDP、显示器关闭、电池节省、屏保运行、用户手动暂停 → 直接暂停。
2. 独占全屏：`SHQueryUserNotificationState` 返回 `QUNS_RUNNING_D3D_FULL_SCREEN` → 默认暂停全部；若要只暂停前台窗口所在显示器，需结合 `GetForegroundWindow` + `MonitorFromWindow`（该 API 本身不告诉你是哪块屏）。
3. 无边框全屏：前台窗口可见、未 cloaked、不是 shell/工具窗口、`GetWindowRect`/`DWMWA_EXTENDED_FRAME_BOUNDS` 覆盖 `rcMonitor` ≥ 98% → 暂停。
4. 被普通窗口覆盖：仅在用户开启“最大化也暂停”时，用 `rcWork` 覆盖率判断；默认关闭，避免误暂停。
5. 应用规则：进程名 allow/deny；`pause`/`ignore`；`kill` 不做（1.0）。

去抖：

- 进入全屏后 **1.5s** 才暂停，避免 Alt+Tab 瞬间闪烁。
- 退出全屏后 **1.0s** 才恢复，避免游戏切换分辨率/全屏窗口重建时抖动。
- 每次判定记录原因集合；任何原因未清除都不恢复。

#### 1.11.2 显存观测

- 每个 Host 用 `IDXGIAdapter3::QueryVideoMemoryInfo(nodeIndex, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &info)` 上报 `Budget/CurrentUsage`（单 GPU 时 `nodeIndex=0`；多 GPU 按节点）；主进程聚合。
- 监听 `RegisterVideoMemoryBudgetChangeNotificationEvent`，预算变化时重新评估；不要高频轮询。
- 释放顺序（MSDN 明确要求）：`ID3D11DeviceContext::ClearState()` → 释放资源 → `Flush()` → `IDXGIDevice3::Trim()`；否则 D3D 可能延迟销毁对象。
- 交换链释放：`ResizeBuffers(0, 0, 0, DXGI_FORMAT_UNKNOWN, 0)`；恢复时按新尺寸重建。
- WebView2：不要混用 `TrySuspendAsync` 和 `MemoryUsageTargetLevel`（MSDN 明确不建议）。暂停用 `controller.IsVisible=false` + `TrySuspendAsync`；恢复用 `IsVisible=true`（会自动 resume）。`MemoryUsageTargetLevel=Low` 只用于“仍要跑脚本的低功耗”场景。
- 视频：`pause=yes` 只停播，不释放解码器；`stop`/`vid=no` 才释放；彻底释放靠杀 Host。
- WebView2 的 GPU 进程显存不在 Host 进程内，`QueryVideoMemoryInfo` 看不到；精确统计需要 PDH `GPU Adapter Memory` 计数器或 `GPUView` 人工分析。1.0 用“Host 工作集 + 适配器总用量（PDH，可选）+ 终止策略”兜底，不承诺精确到 MB。

#### 1.11.3 静态快照兜底

这是“释放显存但不黑屏”的关键。

- 启动时用 `IDesktopWallpaper::GetWallpaper(monitorId)` 保存每个显示器的原始静态壁纸和位置。`monitorId` 来自 `GetMonitorDevicePathAt`，要先用设备路径/几何与 `DisplayMonitor` 匹配；不要假定 `\\.\DISPLAY1` 就是 `IDesktopWallpaper` 的 ID。
- 每个壁纸首次出帧后（或导入时）生成该显示器的快照：
  - 视频：libmpv `screenshot-to-file`；
  - Web：`CoreWebView2.CapturePreviewAsync`；
  - 场景：Phase 3 用离屏 RT + WIC 编码；v1 用作者提供的 `preview.png`；
  - 图片：复制原图。
- 在 `Unload`/`Terminated`/Explorer 重启/崩溃前，用 `IDesktopWallpaper::SetWallpaper(monitorId, snapshot)` 把桌面静态图换成快照；动态窗口仍覆盖时用户看不到切换。
- 恢复时先起 Host；静态图在 Host 窗口下方，不需要移除，Host 首帧后自然覆盖。若 Host 首帧前有透明区域，先把 Host 背景设为黑色/快照色，避免露出桌面闪烁。
- 退出应用/卸载时恢复原始壁纸；若用户运行期间手动换了壁纸，提供“保留当前壁纸”选项，避免覆盖用户选择。
- 默认开启静态兜底（设置可关闭）；检测到 Windows Spotlight/幻灯片时提示，避免用户误以为壁纸被永久改掉。

### 1.12 三个渲染后端

#### 1.12.1 视频：libmpv

- 使用 `libmpv-2.dll`，P/Invoke：`mpv_create`、`mpv_set_option_string`、`mpv_initialize`、`mpv_command`、`mpv_set_property_string`、`mpv_observe_property`、`mpv_wait_event`、`mpv_terminate_destroy`、`mpv_free`。
- 在 `mpv_initialize` 之前设置 `wid=<HostWindow Hwnd>`；之后不要换窗口。窗口尺寸变化由 mpv 自己处理；我们只保证 HWND 尺寸正确。
- 关键选项：`--no-config`、`--load-scripts=no`、`--ytdl=no`、`--osc=no`、`--input-default-bindings=no`、`--input-cursor=no`、`--loop-file=inf`、`--keep-open=yes`、`--hwdec=auto-safe`、`--vo=gpu`、`--gpu-api=d3d11`、`--stop-screensaver=no`、`--terminal=no`、`--audio-client-name=Wallerpaper`。`wid` 嵌入时通常不需要 `--force-window`/`--geometry`；这两个保留给 `mpv.exe` 兜底路径。
- 暂停/恢复：`pause=yes/no`；释放：`stop` 或 `vid=no`；恢复：`loadfile` + seek 到保存位置。
- 音频：默认静音；只有“音频所有者”显示器可出声；`aid=no` 静音比 `volume=0` 更彻底（Lively 的做法）。
- 截图：`screenshot-to-file`；用于静态快照/缩略图。
- 适配器：多 GPU 时优先 `--d3d11-adapter`（需验证当前 mpv 构建支持）；不支持则用默认并记录。
- HDR：优先 `--vo=gpu-next` + `--target-colorspace-hint` 系列；SDR 桌面默认 tone-map；混合 HDR/SDR 显示器单独测试（mpv 文档只推荐在 gpu-next 下使用该 hint）。
- 打包：`libmpv-2.dll` 及其依赖 DLL 一起随应用分发；干净 VM 验证，不依赖用户系统里的 mpv。
- 许可：本项目 GPL-3.0；libmpv 是 GPLv2+，可通过 “or later” 与 GPL-3.0 兼容。仍需确认 FFmpeg 构建与所有第三方组件、随附源码/许可/NOTICE。`IVideoEngine` 保留 `mpv.exe` 子进程作为强隔离/调试兜底，不再是许可规避手段；Media Foundation 用于缩略图/极端兼容兜底。

#### 1.12.2 Web：WebView2

- Host 用 WinForms 只提供消息循环/`SynchronizationContext`；WebView2 用 **raw `CoreWebView2Controller`**，不用 WinForms WebView2 控件，避免反射拿私有 controller。
- 初始化：`CoreWebView2Environment.CreateAsync(null, userDataFolder, options)` → `CreateCoreWebView2ControllerAsync(form.Handle)`；设置 `Bounds`、`IsVisible`、`RasterizationScale`、`ShouldDetectMonitorScaleChanges=false`、`DefaultBackgroundColor`。
- 本地内容：`SetVirtualHostNameToFolderMapping(host, folder, Allow)` + `https://<hash>.localhost/index.html`；不要 `file://`。
- 安全：关闭 DevTools/右键/快捷键/状态栏/下载/弹窗/权限/自动填充；`AreHostObjectsAllowed=false`；不注入 host object；`WebResourceRequested` 在离线模式下阻断非本地请求；`NavigationStarting` 白名单。
- 暂停：`IsVisible=false` → `TrySuspendAsync()`；若返回 `false`（页面仍有脚本在跑、best-effort 失败），记录并降级到关闭页面/杀 Host。恢复：`IsVisible=true`。不要同时用 `MemoryUsageTargetLevel`。
- 崩溃：`ProcessFailed`；`Unresponsive` 在暂停期间可忽略；`BrowserProcessExited`/`RenderProcessExited` 重建 Host。
- 输入：v1 只做“光标效果”，用 `AddScriptToExecuteOnDocumentCreatedAsync` 注入一个小 JS shim，再通过 `PostWebMessageAsJson` 把光标位置发给页面（shim 派发 `mousemove`/pointer 事件）；页面→Host 的消息只做日志/校验，不执行命令。点击交互用热键模式，不依赖 `PostMessage` 到 `Chrome_WidgetWin_1`。`CoreWebView2CompositionController.SendMouseInput` 是官方注入路径，但需要 composition 宿主，Phase 2 spike 评估。
- 内存：每个壁纸独立 UserDataFolder 隔离最好但内存高；提供“共享 profile（可信壁纸）”选项；限制同时运行的 Web 壁纸数量。共享 profile 时 WebView2 可能共享渲染进程，启动/暂停会互相影响，需要串行化（Lively 在 per 模式先关闭再创建，正是这个原因）。
- 遮挡/节流：窗口在 DefView 之下，Chromium 可能判定为 occluded/background 并降低定时器频率；测试 `--disable-backgrounding-occluded-windows`、`--disable-renderer-backgrounding`、`--disable-background-timer-throttling`，只在播放时启用，暂停时不要用（避免空转）。
- HDR：WebView2 内容按 SDR 处理；HDR 显示器上由 DWM/Chromium tone-map，颜色可能不一致；不要承诺 Web 壁纸 HDR 精确。
- 运行时：`GetAvailableBrowserVersionString()` 检测；缺失时提示安装 Evergreen Bootstrapper；安装器可捆绑。

#### 1.12.3 2D/Shader：D3D11

- 设备：`D3D11CreateDevice`，按显示器选择适配器（`IDXGIAdapter::EnumOutputs` 匹配 HMONITOR）；feature level 11_0/10_0；debug 构建开 `D3D11_CREATE_DEVICE_DEBUG`。
- 交换链：`IDXGIFactory2::CreateSwapChainForHwnd`；`DXGI_SWAP_EFFECT_FLIP_DISCARD`；`BufferCount=2`；`B8G8R8A8_UNORM`；`DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT`；`SetMaximumFrameLatency(1)`。
- raised desktop 的 `WS_EX_LAYERED` 与 flip model 的兼容性是 Phase 0 spike；失败则用 `DXGI_SWAP_EFFECT_DISCARD` 或 DirectComposition 路径。
- 2D：动态顶点/索引缓冲 + sprite batch + alpha blend + 线性采样；文本用预渲染图集或 DirectWrite（Phase 3 可选），不要为文本引入完整 D2D。
- 帧调度：动画/时间驱动场景 60/30fps；纯静态或低变化场景用 dirty flag 按需重绘；暂停时 0 呈现。
- Shader 壁纸：全屏三角形 + `ps_5_0`；uniforms：`time`、`delta`、`resolution`、`mouse`、`audio[16]`；常量缓冲 16 字节对齐。
- 编译：`D3DCompile`（d3dcompiler_47，系统自带）；缓存 DXBC 到 `%LocalAppData%`，key = source+defines+target 的 SHA-256；错误 blob 写日志并显示错误壁纸。
- 资源限制：纹理 ≤4096×4096、单场景总纹理 ≤512MB、层数 ≤256、shader ≤1MB、禁止外部 URL/可执行文件。
- TDR/设备丢失：捕获 `DXGI_ERROR_DEVICE_REMOVED/RESET`；重建设备一次；再失败禁用该场景；GPU hang 无法在进程内超时，靠独立进程 + 系统 TDR 隔离。
- 许可/库：1.0 使用 Vortice.Windows（D3D11/DXGI/D3DCompiler/WIC 一套，MIT）；Silk.NET 仅作学习/备选，不进入 1.0 依赖。

### 1.13 输入模型

- 默认：壁纸不接收鼠标/键盘；桌面交互全部由 Explorer 处理。
- 光标效果：RawInput（`RIDEV_INPUTSINK`）读取全局光标；**默认只注册鼠标**，键盘只在 Web 交互模式显式开启。主进程按显示器坐标转换后，通过 IPC 以 30Hz 发给对应 Host；Host 再给渲染器。
  - 场景：直接读共享内存/IPC 的 `mouse` uniform。
  - Web：`PostWebMessageAsJson` + JS shim；不伪造 `WM_MOUSEMOVE` 给 Chromium 内部窗口。
  - 视频：一般不需要；可做轻微视差。
- 点击交互（可选）：默认仍保持 z-order 在 DefView 之下，通过 RawInput + JS pointer/兼容消息把点击转发给壁纸；**不把窗口提到图标之上**。若页面必须真实命中测试，提供“全屏交互”临时模式并明确提示图标不可点（仅建议单屏/全屏应用时使用）。
- 键盘：只对 Web 交互模式转发；默认不转发，避免影响游戏/输入法。
- 绝不做：全局低层鼠标钩子（`WH_MOUSE_LL`）、`SendInput` 移动用户光标、`DebugActiveProcess` 挂起浏览器进程、注入 DLL。

### 1.14 存储与内容格式

```text
%LocalAppData%\Wallerpaper\
├─ appsettings.json          # 全局设置（schemaVersion）
├─ library.json              # 库索引（可重建）
├─ monitors.json             # 显示器稳定 ID → 分配
├─ logs\
├─ cache\
│  ├─ thumbnails\
│  ├─ snapshots\
│  └─ shaders\
├─ profiles\web\<wallpaperId>\   # WebView2 UserDataFolder
└─ crashes\
```

壁纸包 `.wlpkg`（zip）：

```text
manifest.json
preview.jpg
video.mp4 | index.html + assets/ | scene.json + shaders/ + textures/
```

`manifest.json`：

```json
{
  "formatVersion": 1,
  "id": "guid",
  "type": "video|web|scene",
  "title": "…",
  "author": "…",
  "entry": "video.mp4",
  "preview": "preview.jpg",
  "tags": ["…"],
  "properties": {}
}
```

导入规则：

- 只允许白名单扩展名；拒绝 `.exe/.dll/.bat/.cmd/.com/.scr/.ps1/.vbs/.lnk/.url/.msi` 等可执行/脚本/快捷方式（Web 壁纸需要的 `.js` 在 WebView2 沙箱内运行，单独允许）。
- 拒绝 Zip Slip（`..`、绝对路径、盘符、UNC）、Zip Bomb（解压总量/文件数上限）、符号链接/重解析点。
- 所有文件落到库根目录下的新 GUID 目录；不覆盖已有目录；导入完成后原子重命名。
- 计算 SHA-256；文件移动/删除时更新索引；外部引用壁纸记录绝对路径 + mtime + size，变化时提示重新导入。

设置：`System.Text.Json` + `JsonSerializerOptions`（严格、忽略大小写关闭）；原子写（临时文件 + `File.Replace`）；schemaVersion + 迁移函数；未知字段保留（向前兼容）。

### 1.15 安全模型

| 面 | 威胁 | 措施 |
|---|---|---|
| Web 内容 | 恶意 HTML/JS、隐私窃取、CSRF 本地服务 | Chromium 沙箱；关闭 host object/DevTools/下载/弹窗/权限；本地虚拟主机；离线模式阻断外网；每壁纸 UDF；CSP 建议 |
| 壁纸包 | Zip Slip/Bomb、可执行文件、路径穿越 | 白名单、路径规范化、大小/数量上限、拒绝重解析点、无 DLL |
| 视频解码 | 恶意媒体文件触发 mpv/FFmpeg 漏洞；在线流 SSRF | 独立 Host 进程；普通用户权限；及时更新 mpv/FFmpeg；默认只允许本地文件，在线流需显式开启 + 域名白名单；禁用 ytdl/脚本 |
| IPC | 伪造命令、路径越界 | 匿名管道、枚举命令、库根路径校验、长度上限、版本握手 |
| DLL | 侧加载/劫持 | `SetDefaultDllDirectories` + `AddDllDirectory`；libmpv 全路径加载 + 哈希校验；不用当前目录 |
| 更新 | 供应链/降级 | HTTPS、代码签名、SHA-256、Velopack 签名包、禁止未签名更新 |
| 隐私 | 日志/崩溃转储含路径 | 本地存储、默认不上传、支持包需用户确认、可关闭遥测 |
| 权限 | 管理员/注入/杀软误报 | 全程序普通用户权限；不注入、不 hook、不调试；签名；提交微软/杀软白名单 |

### 1.16 可观测性

- 每进程日志：`logs\app-*.log`、`logs\host-<pid>-<type>.log`；JSON lines；滚动 10MB×5。
- 关联字段：`SessionId`、`InstanceId`、`HostPid`、`WallpaperId`、`MonitorId`。
- 指标：FPS、丢帧、Present 时间、显存预算/用量、CPU、工作集、句柄数、DeviceLost 次数、暂停原因、恢复耗时。
- 调试 overlay：`Ctrl+Alt+D` 显示每显示器状态/指标；仅调试构建或高级设置开启。
- 崩溃：Host 未处理异常时写 minidump；WER LocalDumps 作为补充（需管理员配置）；下次启动收集并提示导出支持包。
- 支持包：日志 + 设置（脱敏）+ 显示器拓扑 + GPU 信息 + 崩溃转储；用户确认后生成 zip。

---

## 2. 开发阶段划分

### 2.1 总览

假设团队：1 名技术负责人 + 2 名 C#/Win32 工程师 + 1 名 UI/UX（可兼职）+ 1 名 QA（半职）。  
纯单人开发：把下面所有“周”乘以约 1.8–2.2。

| 阶段 | 周期 | 目标 | 关键交付 | 出口标准 |
|---|---|---|---|---|
| Phase 0 | 3–4 周 | 杀掉技术不确定性 | 6 个 spike + 测量报告 + ADR | 每条高风险路径有“可行/不可行/兜底”结论 |
| Phase 1 | 5–6 周 | 骨架 + 视频 MVP | Alpha：3 屏视频、Job/IPC、经典+raised 挂靠、托盘/库 | 8h soak；杀 Host 20 次不崩 UI；图标 100% 可点 |
| Phase 2 | 4–5 周 | Web 壁纸 | Beta 1：Web 渲染、安全、挂起、预览、DPI | 3 个 Web 壁纸 4K；安全清单；8h soak |
| Phase 3 | 6–8 周 | 2D/Shader | Beta 2：场景格式、D3D11、HLSL 缓存、样例 | 1080p/4K 60fps；TDR 可恢复；无泄漏 |
| Phase 4 | 4–5 周 | 生命周期硬化 | RC：全屏/显存/Explorer/DPI/热插拔/诊断 | 全矩阵通过；24h/72h soak；恢复时间达标 |
| Phase 5 | 4–5 周 | 产品化 | 1.0：安装/更新/签名/本地化/文档 | 干净 VM 安装/升级/回滚/卸载；无管理员权限 |
| Phase 6 | 可选 | 增值 | 音频可视化、span/duplicate、屏保、HDR、插件 | 不影响 1.0 稳定性 |

总计：约 **26–33 周（6–8 个月）**。1.0 已确认三后端；若中途砍掉场景，MVP 可缩到 **12–16 周**。  
时间不含 GPL 合规复核、代码签名证书采购、硬件采购/兼容性实验室搭建。

### 2.2 Phase 0：风险验证（3–4 周）

目标：**在写产品代码之前，把“能不能做”变成“怎么做、失败怎么办”**。

#### Spike A：WorkerW / raised desktop / 图标交互

- 在 Win10 22H2、Win11 23H2、Win11 24H2/25H2 上：
  - `SendMessageTimeout(progman, 0x052C, 0xD, 0x1, ...)` 是否生成 WorkerW；
  - `FindWindowEx(0, top, "WorkerW", 0)` 找到的是否正确；
  - `WS_EX_NOREDIRECTIONBITMAP`/`WS_EX_LAYERED` 判定 raised desktop 是否可靠；
  - 经典路径 child of WorkerW、raised 路径 layered child of Progman 的可见性/z-order；
  - 图标单击/双击/右键/框选/Alt+Tab/任务栏/Win+D。
- 产出：窗口树 dump、失败模式、回退策略、逐 build 的兼容表。

**验收**：至少 2 台物理机（Intel + NVIDIA/AMD）和 2 个 Win11 build 上壁纸可见、图标可点、无焦点窃取；失败 build 有明确兜底（静态壁纸 + 提示），不黑屏。

#### Spike B：D3D11 在挂靠窗口上的呈现

- 经典 WorkerW child + `CreateSwapChainForHwnd`（FLIP_DISCARD）；
- raised desktop 的 `WS_EX_LAYERED` + `SetLayeredWindowAttributes(255)` 下，FLIP_DISCARD 是否正常；
- 失败时 `DXGI_SWAP_EFFECT_DISCARD` / DirectComposition 路径；
- Resize/DPI/设备丢失/多显示器/多 GPU。
- 产出：可运行的 3 个 sample（纯色、纹理、HLSL），帧时间/CPU/GPU 数据。

**验收**：目标机器上 1080p/4K 稳定 60fps；切换 DPI/分辨率不黑屏；`DXGI_ERROR_DEVICE_REMOVED` 能重建一次。

#### Spike C：WebView2 宿主/DPI/挂起

- WinForms 消息循环 + raw `CoreWebView2Controller`；
- `RasterizationScale` 在 100/125/150/175/200/225/250% 与文本缩放 100–225% 下的清晰度/几何；
- `IsVisible=false` + `TrySuspendAsync` 的 CPU/内存/恢复时间；
- 本地 `SetVirtualHostNameToFolderMapping` + 安全设置；
- `CapturePreviewAsync` 快照。
- 产出：WebView2Host POC + 测量报告 + 推荐宿主方案（raw vs WinForms 控件）。

**验收**：4K 页面文字清晰；暂停 10s 后 CPU <1%；恢复 <500ms；无白屏/黑屏闪烁；Runtime 缺失时错误可读。

#### Spike D：libmpv 嵌入与释放

- `libmpv-2.dll` + `wid` + `--no-config` + `--hwdec=auto-safe`；
- 1080p/4K H.264/HEVC/AV1/VP9、GIF、10-bit/HDR；
- `pause` / `stop` / `vid=no` / 杀进程 四种释放级别的显存/恢复时间；
- 多实例（3 屏）CPU/GPU/VRAM；
- 许可：确认 GPLv2+ 与 GPL-3.0 兼容、FFmpeg 构建、源码/许可清单。
- 产出：VideoHost POC + 兼容性清单 + 许可结论。

**验收**：目标机器 4K 视频 CPU <8%（硬解）；暂停后 30s 内显存明显下降或可杀 Host；恢复 <2s；无音频爆音。

#### Spike E：Job/IPC/崩溃隔离

- `CreateJobObject` + `KILL_ON_JOB_CLOSE` + `DIE_ON_UNHANDLED_EXCEPTION`；
- stdio 长度前缀 MessagePack 往返；心跳；杀掉 Host/主进程/Explorer；
- 子进程 stderr drain；孤儿 WebView2/mpv 检查。
- 产出：可复现的 kill 矩阵 + 进程树 dump。

**验收**：杀 Host 20 次 UI 不崩；杀主进程后 5s 内所有子进程消失；无孤儿 `msedgewebview2.exe`/`mpv`；Explorer 重启后可按预案恢复。

#### Spike F：全屏检测与暂停延迟

- `SHQueryUserNotificationState` 独占全屏；
- 无边框全屏：`GetForegroundWindow` + `GetWindowRect` + `DWMWA_CLOAKED` + 扩展边框；
- 多显示器、Alt+Tab、分辨率切换、HDR 游戏；
- 暂停/恢复延迟、误判率。
- 产出：判定算法 + 阈值 + 应用规则样例。

**验收**：目标游戏进入全屏 2s 内暂停；退出 3s 内恢复；桌面/最大化窗口不误暂停（默认配置）；多屏只暂停受影响屏。

#### Phase 0 出口

- 6 个 spike 至少 5 个“通过”，剩余 1 个有明确兜底；
- 架构文档按实测修订；
- ADR-001~010 完成；
- GPL-3.0 合规清单完成：Lively 复用范围、libmpv/FFmpeg 源码/许可、第三方 NOTICE；
- 重新估算 Phase 1–5 工期。

### 2.3 Phase 1：骨架 + 视频 MVP（5–6 周）

交付：

1. Solution/项目结构、`Directory.Build.props`、中央包管理、CI（build + unit test）。
2. `Contracts` + `Interop` + 日志 + 设置 + DI + 单实例 + 托盘 + 隐藏消息窗口。
3. `MonitorService`（稳定 ID/DPI/热插拔事件）+ 设置里的显示器布局 UI。
4. `DesktopService`：经典 WorkerW + raised desktop 双路径；挂靠校验；Explorer 监听骨架。
5. `RenderHostManager`：Job + stdio + 握手 + 心跳 + 崩溃预算 + 退避重启。
6. `VideoRenderer`：libmpv、播放/暂停/静音/音量/截图、fit 模式、首帧事件。
7. `StaticFallbackService`：原壁纸保存、视频快照、`IDesktopWallpaper` 设置/恢复。
8. 库：导入视频/GIF/图片、缩略图、按显示器应用、基础设置页。
9. 基础暂停：用户手动、锁屏、RDP、显示器关闭、全屏（独占 + 前景覆盖）。
10. 诊断：每进程日志、Host 状态页、导出日志。

验收（Alpha）：

- 3 台显示器（含 1 台 4K、1 台竖屏）各自播放不同视频；重启应用后恢复分配。
- 杀 Host 20 次：UI 不崩、自动重启、静态快照兜底、无孤儿进程。
- 图标单击/双击/右键/框选 1000 次无失败；壁纸不抢焦点、不出现在 Alt+Tab。
- 经典 + raised desktop 各至少 1 个 build 通过；失败时静态壁纸 + 可读错误。
- 1080p 视频 CPU（硬解）<3% / 单核；4K <8%；显存单实例 1080p <250MB、4K <700MB（参考机）。
- 暂停 <150ms；恢复 <800ms（pause 级别）。
- 8h soak：私有字节增长 <5%，句柄增长 <50，无进程泄漏。
- 日志无未处理异常；崩溃有 dump/退出码。

### 2.4 Phase 2：Web 壁纸（4–5 周）

交付：

1. `WebRenderer`：WebView2 环境/控制器、UserDataFolder、虚拟主机、导航白名单。
2. 安全基线：关闭 DevTools/右键/快捷键/下载/弹窗/权限/自动填充；`AreHostObjectsAllowed=false`；离线模式阻断外网。
3. 暂停/恢复：`IsVisible=false` + `TrySuspendAsync`；恢复自动 resume；`ProcessFailed` 处理。
4. DPI：`RasterizationScale` + 文本缩放；`Bounds`/Resize；混合 DPI 切换。
5. 光标桥：`PostWebMessageAsJson` + JS shim；30Hz 合并；可选交互模式（热键）。
6. 快照：`CapturePreviewAsync`；静态兜底；缩略图。
7. 库：本地 HTML 文件夹/zip、在线 URL（可选）、预览面板。
8. 限额：同时运行 Web 壁纸数量、内存/CPU 提示、共享/独立 profile 选项。

验收（Beta 1）：

- 3 个 Web 壁纸（本地 three.js/Canvas/视频背景）在 3 屏同时运行 8h；无崩溃、无白屏。
- 4K 页面文字清晰；100–250% DPI + 文本缩放 100–225% 无错位。
- 暂停 10s 后 CPU <1%；恢复 <500ms；显存/内存明显下降或 Host 可被终止。
- 安全清单全过：无 host object、无 DevTools、无下载/弹窗、非白名单导航被拒、离线模式无外网请求。
- 杀 WebView2 浏览器/渲染进程：Host 重启或可读错误；不影响其他显示器。
- 8h soak：无 `msedgewebview2.exe` 孤儿；内存增长 <10%。

### 2.5 Phase 3：2D 场景/着色器（6–8 周）

交付：

1. `.wlpkg` 场景格式 v1：`scene.json`、`shaders/`、`textures/`、`preview.png`；JSON Schema + 校验器。
2. `SceneRenderer`：D3D11 设备/交换链/RT、sprite batch、纹理加载（WIC）、alpha blend、正交投影。
3. Shader 壁纸：全屏三角形、uniforms（time/delta/resolution/mouse/audio）、HLSL 编译/缓存/错误处理。
4. 资源限制：纹理尺寸/总量、层数、shader 大小、禁止外部 URL/可执行文件。
5. 帧调度：60/30fps cap、vsync、帧延迟 waitable object、暂停门控。
6. 设备丢失/TDR：重建一次、失败禁用、错误壁纸。
7. 样例：gradient/plasma、粒子、parallax、音频可视化占位（无音频也可跑）。
8. `Wallerpaper.Tools`：`pack`/`validate`/`thumbnail` CLI；场景作者文档。
9. 预览：作者 `preview.png`；Phase 3 后段可做离屏快照。

验收（Beta 2）：

- 参考机 1080p/4K 60fps；30fps cap 时 CPU/GPU 明显下降；暂停时 0 呈现。
- 10 个样例场景（含 1 个故意死循环/超重 shader）加载：坏 shader 被拒绝或 TDR 后恢复，不影响其他屏/主进程。
- 100 次场景切换：无设备泄漏、无句柄增长、无黑屏。
- 设备丢失模拟（`dxgi` debug / 驱动重启）：重建一次成功；连续失败禁用并提示。
- 场景包校验：Zip Slip/Bomb/超大纹理/外部 URL/可执行文件全部拒绝。
- 8h soak：显存回落到基线 ±10%。

### 2.6 Phase 4：生命周期与稳定性（4–5 周）

交付：

1. `FullscreenWatcher`：独占 + 无边框 + 每显示器 + 应用规则 + 去抖；性能优化（前台快路径/枚举慢路径）。
2. `VramBudgetService`：预算/用量、压力阈值、分级释放、终止策略、恢复预算。
3. Explorer 重启完整处理：`TaskbarCreated` + WinEventHook + PID 比对 + 退避 + 托盘重加 + 静态兜底。
4. 锁屏/RDP/电源/显示器关闭/电池节省/屏保：通知 + 轮询兜底。
5. 热插拔/DPI/分辨率/主屏切换/旋转：重算 + Resize + 恢复分配。
6. 崩溃转储、safe mode、崩溃预算、支持包、诊断页。
7. 性能：空闲 CPU/GPU、启动时间、暂停/恢复、3×4K 压力、帧 pacing。
8. 兼容性：ExplorerPatcher/StartAllBack/TranslucentTB/其他壁纸引擎共存提示。

验收（RC）：

- Explorer 杀 20 次：5s 内壁纸/图标/托盘恢复；无崩溃；无孤儿；显存稳定。
- DPI 矩阵：100/125/150/175/200/225/250%，混合 DPI，竖屏，负坐标；窗口与显示器误差 0–1px。
- 全屏矩阵：独占/无边框/多屏/HDR/Alt-Tab/分辨率切换；暂停 <2s，恢复 <3s；桌面不误暂停。
- 暂停 60s + 全屏：Host 终止，静态快照可见；退出游戏 3s 内恢复。
- 24h soak（3 屏，视频+Web+场景轮换）：私有字节 <5%、GPU 内存 <10%、句柄 <50、无孤儿。
- 1000 次暂停/恢复、100 次壁纸切换、100 次 Explorer 重启：无泄漏/崩溃。
- safe mode：连续 3 次启动崩溃后进入安全模式，不加载壁纸，可导出诊断。
- 所有错误路径有用户可读提示 + 日志 + 错误码。

### 2.7 Phase 5：产品化（4–5 周）

交付：

1. Velopack 安装/更新：per-user、无管理员、开始菜单、可选开机启动、卸载保留库。
2. 前置检查：Windows App Runtime（自包含则跳过）、WebView2 Runtime、Windows 版本、GPU/驱动；VC++ 仅在 libmpv/原生依赖需要时检测安装。
3. 代码签名：主程序、Host、安装器、更新包；SmartScreen 提交。
4. 首次运行向导：库目录、导入、分配到显示器、性能预设、诊断同意。
5. 本地化：中文/英文；`.resw`；日期/数字/路径。
6. 可访问性：键盘导航、高对比、减少动画、屏幕阅读器名称。
7. 文档：用户手册、故障排查（壁纸不显示/Explorer 重启/WebView2 缺失/性能）、隐私政策、第三方许可。
8. 更新/回滚：beta/stable 通道、协议版本兼容、失败回滚、更新时暂停/恢复壁纸。
9. Beta 计划：遥测 opt-in、崩溃收集、反馈入口、已知问题。

验收（1.0）：

- 干净 Win10 22H2 / Win11 23H2/24H2/25H2 VM：安装→首次运行→设置视频/Web/场景→重启→升级→回滚→卸载，全程无管理员权限。
- 缺 WebView2/VC++ 的机器：安装器能检测并引导；不出现不可解释的黑屏。
- SmartScreen/杀软：签名有效；Defender 扫描无告警；至少 2 家第三方杀软抽查。
- 升级中断/断电：下次启动可恢复；不丢库/设置。
- 卸载：可选择保留库；静态壁纸恢复原始；无残留进程/服务。
- 文档齐全；错误信息可读；隐私默认最小化。

### 2.8 Phase 6：可选增值（不阻塞 1.0）

- 音频可视化：WASAPI loopback + FFT，共享内存把频谱给场景/Web；默认关闭。
- Now Playing：GSMTC（WinRT）传给 Web/场景；默认关闭。
- `duplicate`：多屏同壁纸、同步播放、单音频源。
- `span`：仅同 DPI/同刷新率；混合 DPI 明确不支持。
- 屏保：独立 `.scr` 宿主，复用渲染器；与桌面壁纸互斥。
- HDR 场景：`IDXGIOutput6` + `R10G10B10A2` + `SetColorSpace1`；tone mapping。
- 插件 SDK：只允许受控的 scene/web 包，不允许任意 DLL。
- ARM64：等 libmpv/WebView2/驱动生态验证后再做。
- Wallpaper Engine `.pkg` 导入：**不做**。格式 proprietary/加密，法律与技术风险高；只支持通用视频/图片/HTML/自有场景。

### 2.9 验收基线（参考机）

参考机：Windows 11 24H2，i7-12700，RTX 3060 12GB，32GB RAM，NVMe，3 屏（1080p60 / 1440p165 / 4K60）。  
以下为 **provisional**，Phase 0 后按实测校准；不构成对所有硬件的承诺。

| 指标 | 目标 |
|---|---|
| 冷启动到托盘 | <1.5s |
| 应用壁纸到首帧（视频） | <2.0s |
| 应用壁纸到首帧（Web 本地） | <2.5s |
| 应用壁纸到首帧（场景） | <1.0s |
| 全屏暂停延迟 | <2s（含去抖）；用户手动 <150ms |
| 全屏恢复 | <3s（含去抖）；从 pause 级别 <800ms |
| 1080p 视频 CPU（硬解） | <3% 单核 |
| 4K 视频 CPU（硬解） | <8% 单核 |
| 1080p 视频显存 | <250MB/实例 |
| 4K 视频显存 | <700MB/实例 |
| Web 1080p 工作集 | <350MB/实例（独立 profile） |
| 场景 1080p 显存 | <250MB/实例 |
| 场景 1080p 帧率 | 60fps（参考场景） |
| 24h 私有字节增长 | <5% |
| 24h GPU 内存增长 | <10% |
| 24h 句柄增长 | <50 |
| Explorer 重启恢复 | <5s |
| 杀 Host 恢复 | <2s；3 次/5min 后禁用该壁纸 |
| 图标交互 | 1000 次点击 100% 成功；0 次焦点窃取 |

### 2.10 Definition of Done / 质量门禁

每个 PR：

- `dotnet build -warnaserror`；nullable 开启； analyzers 无新增警告。
- 单元测试覆盖核心逻辑（协议/路径校验/DPI 数学/状态机）。
- 新增 P/Invoke 必须有 `SafeHandle`/错误检查/`SetLastError` 语义；禁止裸 `int` 句柄泄漏。
- 新增 Win32 调用必须在本文 §6.1/§6.2 有 MSDN 或 Lively 来源（后续可拆成独立 `API-VERIFICATION.md`）。
- 新增 UI 必须在 100%/150%/200% 下截图检查。
- 新增渲染路径必须有暂停/恢复/设备丢失处理。
- 新增日志不得包含用户文件内容/URL 查询参数（脱敏）。

每个阶段：

- 性能/soak 报告；
- 风险登记册更新；
- ADR 更新；
- 兼容性矩阵更新；
- 发布说明 + 已知问题。

---

## 3. 风险登记册

> 概率/严重度：H/M/L。阶段：预计首次暴露的阶段。  
> “验证”列是**可执行的验收**，不是“注意一下”。

### 3.1 风险矩阵

| ID | 风险 | 影响 | 概率 | 严重度 | 关键缓解 | 验证 | 兜底 | 阶段 |
|---|---|---|---|---|---|---|---|---|
| R1 | WorkerW/Progman 层级随 Windows build 变化 | 壁纸不显示/盖住图标/黑屏 | H | H | 双路径 + 能力探测 + 多策略发现 + 逐 build 矩阵 + 本地 feature flag | 6 个 OS build × 2 GPU；图标 1000 次 | 静态壁纸 + 提示；不覆盖图标 | 0/1/4 |
| R2 | `0x052C` 未文档化，未来失效 | WorkerW 不生成 | M | H | 不单一依赖；先试 Lively 参数再试 `0,0`；已有 WorkerW/Progman 兜底 | 关闭 Explorer 后冷启动、Win11 各 build | 直接用现有 WorkerW/Progman；静态壁纸 | 0/1 |
| R3 | Explorer 重启/崩溃销毁 WorkerW 和 child HWND | 壁纸消失、孤儿进程、托盘丢失 | H | H | TaskbarCreated + WinEventHook + PID 比对 + 退避 + 静态快照 + Job | 杀 Explorer 20 次；游戏全屏时杀 | 停自动恢复；托盘提示；静态壁纸 | 1/4 |
| R4 | 多屏/混合 DPI 错位、模糊、1px 缝 | 视觉错误、用户投诉 | H | H | 全链路 PerMonitorV2；物理像素坐标；MapWindowPoints；GetDpiForWindow；WebView2 scale；自检 | 100–250% + 混合 + 竖屏 + 负坐标 | 单屏模式；提示用户统一缩放 | 0/1/4 |
| R5 | 显存泄漏/多实例爆显存 | 游戏掉帧、驱动重置、系统卡顿 | H | H | 预算/用量遥测；分级释放；Trim；ResizeBuffers(0)；杀 Host；限额 | 24h soak；100 次暂停/恢复；3×4K | 降级到 1080p/30fps；禁用最重实例 | 1/4 |
| R6 | 全屏检测误判/漏判 | 游戏时壁纸偷 GPU，或桌面壁纸被误暂停 | H | M | 独占 + 覆盖 + 去抖 + 应用规则 + 每屏 | 10 款游戏/播放器/浏览器全屏 | 用户手动暂停/白名单 | 1/4 |
| R7 | WebView2 Runtime 缺失/版本不兼容/进程崩溃 | Web 壁纸黑屏 | H | M | 启动检测；安装器引导；ProcessFailed；Host 重启；固定版本选项 | 干净 VM 无 Runtime；杀浏览器进程 | 提示安装；回退静态/视频 | 2/5 |
| R8 | Web 内容安全/隐私 | 恶意页面、数据外发、本地服务 CSRF | M | H | 沙箱设置；无 host object；导航白名单；离线阻断；每壁纸 UDF；CSP | 安全 checklist + 渗透样例 | 禁用在线 Web；只允许本地签名包 | 2 |
| R9 | 恶意/劣质 shader 导致 TDR | 全系统 GPU 重置/黑屏 | M | H | 独立进程；资源上限；加载试渲染；禁 compute；设备丢失恢复；作者签名/审核 | 死循环/超大循环 shader 触发 TDR | 禁用该场景；错误壁纸；提示 | 3 |
| R10 | GPL-3.0 合规：Lively 复用、libmpv/FFmpeg、第三方 NOTICE | 法律/分发风险 | M | M | 保留版权/来源；随附源码/许可；审计 FFmpeg 构建；第三方 NOTICE | GPL 合规清单/法务复核 | 替换不兼容组件；独立进程隔离 | 0/5 |
| R11 | 部署缺 Windows App Runtime/WebView2/VC++/.NET runtime | 安装后打不开/黑屏 | H | H | Windows App SDK 自包含或安装器部署；.NET 10 自包含；WebView2 检测；VC++ 按原生依赖检测 | 干净 Win10/11 VM 安装 | 引导安装；安全模式 | 5 |
| R12 | 进程/句柄/COM 泄漏、孤儿进程 | 长时间运行卡顿/崩溃 | H | M | Job + stdio EOF + 心跳 + Dispose 规范 + 句柄监控 | 24h/72h soak；100 次 kill | 定期重建 Host；Job/TerminateProcess 兜底 | 1/4 |
| R13 | 输入转发抢焦点/影响游戏/杀软误报 | 用户操作受影响 | M | H | 默认不转发；RawInput 非 hook；仅桌面前台；交互模式显式开启 | 游戏/输入法/远程桌面/杀软 | 关闭输入转发 | 2/4 |
| R14 | 静态兜底改壁纸影响 Spotlight/幻灯片/用户设置 | 用户壁纸被改 | M | M | 保存/恢复原壁纸；默认提示；可关闭；检测 Spotlight | 安装/退出/崩溃/重启 | 不改静态壁纸，接受短暂黑屏 | 1/4 |
| R15 | 多 GPU/混合输出/HDR 选错适配器 | 黑屏/性能差/颜色错误 | M | H | 按 HMONITOR 选适配器；HDR 检测；SDR tone-map；Windows 图形首选项 | 双 GPU 笔记本 + HDR 显示器 | 默认 GPU；SDR；提示 | 1/3/4 |
| R16 | 多壁纸音频混音/设备切换爆音 | 用户体验差 | M | M | 单一音频所有者；默认静音；淡入淡出；设备变更监听 | 3 屏 3 视频 + 耳机插拔 | 全部静音，仅手动开启 | 2/4 |
| R17 | 杀软/EDR 误报（SetParent/RawInput/dump） | 安装被拦/进程被杀 | M | H | 不注入/不 hook/不调试；签名；提交白名单；最小权限 | Defender + 2 家第三方 | 提供企业白名单说明 | 5 |
| R18 | 更新/协议版本不匹配 | 新旧 Host 混跑崩溃 | M | M | 协议版本握手；安装器同版本更新；失败回滚 | 升级/回滚/断电 | 拒绝启动旧 Host；提示修复 | 5 |
| R19 | 中文/长路径/Unicode/符号链接 | 导入/播放失败 | M | M | 长路径 manifest；`\\?\`；规范化；拒绝重解析点；测试语料 | 中文/emoji/260+ 字符/网络盘 | 提示移动库到本地短路径 | 1/5 |
| R20 | 锁屏/RDP/睡眠/快速用户切换 | 黑屏/恢复失败/GPU 设备丢失 | H | M | WTS/Power 通知 + 轮询；恢复后校验窗口/设备；1s 延迟 | 锁屏 20 次/RDP/睡眠/切换用户 | 重建 Host；静态壁纸 | 1/4 |
| R21 | Windows 10 已停止支持/新旧 API 差异 | 兼容性/安全问题 | H | M | 明确 min OS；Win11 主支持；Win10 22H2 best-effort；API 动态检测 | Win10 22H2 + Win11 | 不支持 Win7/8；提示升级 | 0/5 |
| R22 | 3×4K/高刷下性能不达标 | 卡顿/风扇/耗电 | H | M | 硬解；帧率上限；质量预设；显存预算；暂停；按 GPU 分级 | 3×4K 压力测试 | 自动降 1080p/30fps；禁用场景 | 1/4 |
| R23 | WebView2 每壁纸 profile 内存过高 | 多 Web 壁纸 OOM | H | M | 共享 profile 选项；限额；暂停卸载；杀 Host | 4 个 Web 壁纸 8h | 限制同时 Web 数量 | 2/4 |
| R24 | 场景格式无人用/内容生态弱 | 功能价值低 | M | M | 先做 Web（three.js/PixiJS）；场景只做低开销/Shader 差异化；样例+文档+工具 | Beta 用户反馈 | 场景降级为可选 | 3/6 |
| R25 | 其他壁纸引擎/Explorer 修改工具冲突 | z-order 被抢/闪屏 | M | M | 检测同类进程/窗口；不主动对抗；提示；重挂 | Wallpaper Engine/Lively/TranslucentTB/ExplorerPatcher | 提示退出冲突软件 | 4/5 |
| R26 | 代码签名/SmartScreen/分发成本 | 安装转化低 | H | M | EV 证书；提前积累信誉；MSIX/商店备选 | 干净机 SmartScreen | 文档引导“仍要运行” | 5 |
| R27 | 视觉/交互设计不达 WinUI 3 预期 | 产品力不足 | M | M | 早期设计系统；Figma；可用性测试；Mica/Fluent 克制使用 | 5 人可用性测试 | 降级为简洁工具风 | 1/5 |
| R28 | 单人/小团队工期估算过于乐观 | 延期 | H | H | Phase 0 后重估；MVP 先视频+Web；砍场景/高级功能 | 每阶段 burn-down | 缩 1.0 范围 | 全程 |

### 3.2 Top 8 深挖

#### R1/R2：WorkerW 与 raised desktop

**为什么难**：这不是 API，是 Explorer 的内部窗口树；不同 Windows build、HDR、Explorer 修改工具都会改变。`0x052C` 没有文档，失败时可能“什么都不发生”，也可能生成多个 WorkerW。

**策略**：

1. **能力探测优先于版本号**：不写 `if (build >= 22621)`；检测 `WS_EX_NOREDIRECTIONBITMAP`、`WS_EX_LAYERED`、DefView 的父窗口、WorkerW 数量/矩形。
2. **多策略发现**：经典 EnumWindows + next WorkerW；Progman 下 WorkerW；Progman 直接作为父；每次校验 class/rect/可见性。
3. **挂靠后自检**：`GetWindowRect` vs `rcMonitor`、`GetClientRect`、`IsWindowVisible`；不一致修正一次，仍失败切换模式。
4. **可视化诊断**：调试构建画 1px 边框/角标 3s；日志输出窗口树；用户“壁纸不显示”向导可导出。
5. **不覆盖图标**：任何失败路径都不能把窗口放到 DefView 之上；宁可静态壁纸。
6. **本地 feature flag**：把“是否尝试 raised 模式/是否用 Progman 兜底”做成设置/启动参数，出问题可让用户或支持人员关闭，不需要联网。

**验证**：Win10 22H2、Win11 23H2/24H2/25H2，经典/raised，HDR 开/关，ExplorerPatcher/StartAllBack 各一台；每组合记录窗口树与截图。

#### R3：Explorer 重启

**为什么难**：WorkerW 被销毁会连带销毁 child HWND；`libmpv` 的 `wid` 绑定的是旧 HWND；WebView2 控制器也绑定旧 HWND。此时“保留进程只重挂”通常不可行。

**策略**：

1. **先保桌面，再保进程**：静态快照立即生效；用户看到的是同一画面，不是黑色。
2. **检测三路**：`TaskbarCreated`、`EVENT_OBJECT_DESTROY`、解锁后 `IsWindow` 校验；用 Explorer PID 区分“真重启”和 DPI 广播。
3. **v1 重启 Host**：简单可靠；退避 0.5/1/2/4s；60s 内 >3 次停止自动恢复。
4. **Job 兜底**：主进程死/退出，子进程全灭；不留下孤儿 WebView2/mpv。
5. **托盘重加**：`Shell_NotifyIcon(NIM_ADD)` 必须在 `TaskbarCreated` 后重做；H.NotifyIcon 也要验证。
6. **全屏时延迟**：游戏全屏中 Explorer 重启，先标记 dirty，等退出全屏再恢复，避免抢 GPU。

**验证**：`Stop-Process -Name explorer -Force` ×20；游戏全屏中杀；DPI 变化触发 `TaskbarCreated` 不误判；检查进程树/句柄/VRAM。

#### R4：多屏 DPI

**为什么难**：WinUI 用 DIP，Win32 用物理像素，WebView2 又用 DIP×RasterizationScale；Explorer 的 DPI 感知可能不同；跨进程 `SetParent` 可能触发 DPI 虚拟化。

**策略**：

1. 全链路 PerMonitorV2；每个进程启动最早期设置；manifest + API 双保险。
2. Core 内部只传物理像素矩形 + `dpiScale`；UI 层最后一步才转 DIP。
3. 子窗口定位用 `MapWindowPoints(HWND_DESKTOP, parent, ...)`；不使用“屏幕坐标直接 SetWindowPos 到 child”的混用。
4. `GetDpiForWindow` 优先；`GetDpiForMonitor` 只作 fallback；文本缩放单独乘。
5. WebView2：`ShouldDetectMonitorScaleChanges=false`，显式 `RasterizationScale`；Resize 顺序固定。
6. 自检：挂靠后比较 `GetWindowRect`/`GetClientRect`/`rcMonitor`；误差 >1px 记录并修正。
7. 测试矩阵固定：100/125/150/175/200/225/250%，4K+1080p 混合，竖屏，负坐标，主屏切换。

**验证**：每个 DPI 组合截图对比；用 1px 网格图检查缝隙；WebView2 用 12px 字体检查清晰度；视频用测试图案检查比例。

#### R5：显存泄漏

**为什么难**：视频解码器、WebView2 GPU 进程、D3D11 资源、交换链、WebView2 profile 都可能持有显存；`pause` 不等于释放；D3D 延迟销毁；驱动/OS 预算会变。

**策略**：

1. **能量化**：每 Host 上报 `QueryVideoMemoryInfo`；主进程聚合；PDH/GPUView 做交叉验证。
2. **分级释放**：Pause → Media → Unload → Terminate；每一级有明确 API 和验收。
3. **正确顺序**：`ClearState` → 释放 → `Flush` → `Trim`；交换链 `ResizeBuffers(0)`。
4. **WebView2**：`IsVisible=false` + `TrySuspendAsync`；需要彻底释放就杀 Host（浏览器进程一起走）。
5. **预算**：按适配器 `Budget` 的 60–70% 设上限；超过则降级/杀最重实例；4K 视频数量限制。
6. **Soak**：24h/72h，轮换/暂停/恢复/Explorer 重启/睡眠；记录基线、峰值、结束值。

**验证**：100 次 pause/resume 后 VRAM 回到基线 ±10%；24h 增长 <10%；全屏 60s 后 Host 终止且静态快照可见；无 `msedgewebview2.exe`/`mpv` 孤儿。

#### R6：全屏检测

**为什么难**：独占全屏、无边框全屏、最大化、多屏、HDR、Alt+Tab、游戏启动器/覆盖层都会影响判定；`SHQueryUserNotificationState` 只覆盖一部分。

**策略**：

1. 组合信号：`QUNS_RUNNING_D3D_FULL_SCREEN` + 前台窗口覆盖 `rcMonitor` + cloaked 检查 + 工具窗口/壳窗口排除。
2. 默认只对“覆盖整个 monitor”暂停；最大化窗口（覆盖 `rcWork`）不暂停，除非用户开启。
3. 去抖 1.5s/1.0s；原因集合；每屏独立。
4. 应用规则：进程名 allow/deny；播放器/浏览器可配置。
5. 性能：前台快路径（`GetForegroundWindow`）优先；`EnumWindows` 只在需要多屏覆盖时用；1s 一次，别 60 次/秒。
6. 日志：记录触发窗口的 class/pid/rect，方便用户报“误暂停”。

**验证**：10 款游戏（独占/无边框）、PotPlayer/MPC/YouTube 全屏、PowerPoint 放映、Alt+Tab、分辨率切换、HDR；误判率 <1%/天（beta 统计）。

#### R7/R8：WebView2

**为什么难**：Runtime 缺失、版本漂移、进程模型复杂、DPI/文本缩放、输入注入没有窗口版官方 API、内存高、页面不可信。

**策略**：

1. 启动检测 Runtime；安装器引导；错误页可读。
2. raw controller + WinForms 消息循环；不用反射私有字段。
3. 安全默认值全部关闭；本地虚拟主机；导航白名单；离线阻断；每壁纸 UDF（或可信共享）。
4. 暂停用 `IsVisible=false` + `TrySuspendAsync`；不要混 `MemoryUsageTargetLevel`。
5. `ProcessFailed` 分类处理；`Unresponsive` 在暂停时忽略；浏览器/渲染进程退出重启 Host。
6. 输入：v1 光标桥（`PostWebMessageAsJson`），点击交互用热键模式；`SendMouseInput` 需 composition 宿主，Phase 2 spike。
7. 限额：同时 Web 壁纸数量、内存提示、共享 profile 选项。

**验证**：无 Runtime 干净 VM；杀浏览器/GPU/渲染进程；4K/DPI 矩阵；安全 checklist；4 个 Web 壁纸 8h。

#### R9：Shader/TDR

**为什么难**：GPU hang 不能在进程内可靠超时；TDR 会重置整个显示子系统；恶意 shader 可以让桌面黑几秒。

**策略**：

1. 独立进程（已经是）；禁 compute（v1）；限制纹理/层数/shader 大小。
2. 加载时 64×64 试渲染 N 帧 + `Flush`；设备丢失则拒绝。
3. 只允许作者签名/审核的场景包；用户自制 shader 需显式“高级模式”警告。
4. 设备丢失：重建一次；再失败禁用场景；记录 `hr`/适配器/驱动版本。
5. 错误壁纸：品红/黑棋盘（壁纸上不放文字）；错误详情走托盘通知 + 库内错误卡片。
6. 监控：TDR 事件（Event Log `Display`/`nvlddmkm`/`amdkmdag`）可选；至少记录 DeviceLost。

**验证**：死循环 shader、超大循环、巨大纹理、除零、越界采样；TDR 后其他屏/UI 不受影响；连续 10 次设备丢失不崩。

#### R10/R11：GPL 合规与部署

**为什么难**：GPL-3.0 下复用 Lively 代码要保留版权/来源；libmpv/FFmpeg 是 GPLv2+ 系，要确认与 GPLv3 兼容并随附源码；Windows App SDK/WebView2/VC++ 前置；代码签名成本。

**策略**：

1. 许可证已定：GPL-3.0；所有衍生代码整体 GPL-3.0。
2. 复用 Lively：保留版权头、LICENSE、来源 commit；新增修改也按 GPL 发布。
3. libmpv/FFmpeg：确认 GPLv2-or-later 兼容、FFmpeg 构建配置、源码获取方式；随附 LICENSE/NOTICE。
4. 安装器检测 WebView2；VC++ 仅在 libmpv/原生依赖需要时安装；.NET 10 自包含。
5. 签名/SmartScreen 提前做；准备企业白名单说明。
6. 更新包签名 + 回滚；协议版本握手。

**验证**：GPL 合规清单/法务复核；干净 VM 安装；升级/回滚；杀软抽查；第三方许可清单。

### 3.3 风险驱动的 Spike 顺序

```text
Week 1: A（WorkerW/raised） + E（Job/IPC）      ← 决定项目能不能成立
Week 2: B（D3D11 layered） + D（libmpv）        ← 决定渲染路径
Week 3: C（WebView2/DPI） + F（全屏）           ← 决定体验
Week 4: 复盘/ADR/重估；补做失败项                ← go/no-go
```

### 3.4 兼容性测试矩阵

| 维度 | 必测 |
|---|---|
| OS | Win10 22H2（best-effort）、Win11 23H2、24H2、25H2 |
| 桌面 | 经典 WorkerW、raised desktop、HDR 开/关 |
| Explorer 修改 | 原版、ExplorerPatcher、StartAllBack、TranslucentTB（至少各 1） |
| 显示器 | 单屏、双屏、三屏、竖屏、4K、混合 DPI、负坐标、主屏切换 |
| GPU | NVIDIA、AMD、Intel iGPU、双 GPU 笔记本 |
| 视频 | H.264、HEVC、AV1、VP9、10-bit、HDR、GIF、损坏文件 |
| Web | 本地 three.js、Canvas、视频背景、在线 URL、恶意导航/弹窗/下载 |
| 场景 | 正常、死循环 shader、超大纹理、设备丢失 |
| 系统状态 | 锁屏、RDP、睡眠/唤醒、快速用户切换、屏保、电池节省 |
| 冲突软件 | Wallpaper Engine、Lively、Rainmeter、TranslucentTB、ExplorerPatcher |
| 安装 | 干净 VM、无 WebView2、无 VC++、升级、回滚、卸载、无管理员 |

---

## 4. 技术选型复核

### 4.1 逐项结论

| 组件 | 原方案 | 结论 | 必须的调整/理由 |
|---|---|---|---|
| UI | WinUI 3 | **已确认（最终）** | Fluent/Mica 开箱即用；托盘/热键/子窗口用 Win32 互操作；Windows App SDK 自包含或安装器部署 |
| 运行时 | .NET 10 LTS | **已确认** | .NET 8 于 2026-11 结束支持；.NET 10 支持到 2028-11；WinUI 3/.NET 10 组合在 Phase 0 验证 |
| 视频 | libmpv | **保留（GPL-3.0 兼容）** | 格式/硬解/HDR 最强；libmpv GPLv2+ 可兼容 GPLv3；`IVideoEngine` 保留 `mpv.exe`/Media Foundation 作为隔离/兜底 |
| Web | WebView2 | **保留，最佳选择** | Chromium 沙箱、系统更新、包体小；CEF 只在固定版本/特殊编解码时考虑 |
| 2D/Shader | Silk.NET DX11 | **改为 Vortice.Windows（已确认）** | Vortice 的 D3D11/DXGI/D3DCompiler/WIC 互操作更完整；Silk.NET 不进入 1.0 依赖 |
| 进程模型 | 每壁纸一进程 | **修正为每“壁纸实例 × 显示器”一进程** | 多屏独立/崩溃域/显存核算都要求进程边界到显示器 |
| IPC | 泛称 IPC | **stdin/stdout + 长度前缀 + MessagePack** | 无网络面/无命名抢占/父死子灭；Lively 对 WebView2 也是 stdio |
| 桌面挂靠 | WorkerW | **保留，但增加 raised desktop 双路径** | Win11 HDR/raised desktop 下经典 WorkerW 不够；Lively 已有实现 |
| 全屏/显存 | 自动暂停 | **改为分级释放 + 静态兜底** | `pause` 不释放显存；杀 Host 必须让桌面仍是同一画面 |
| 安装 | 未定 | **unpackaged + Velopack** | 需要自由启动子进程/写库/开机启动；MSIX 只用于商店 |
| 许可证 | GPL-3.0 | **已确认** | 可复用 Lively 代码；libmpv/FFmpeg GPLv2+ 兼容审计；随附源码/许可/NOTICE |

> D2 已确认 .NET 10 LTS，不再考虑 .NET 8。Phase 0 只需验证 WinUI 3/.NET 10/Vortice/WebView2 的工具链组合；如果 Windows App SDK 当前稳定版对 .NET 10 支持不完整，先验证再决定是否临时回退 TFM，不改变长期目标。

### 4.2 备选方案对比

#### UI：WinUI 3（已选） vs WPF

| 维度 | WinUI 3 | WPF |
|---|---|---|
| 视觉/现代控件 | **更好，Fluent/Mica** | 成熟但偏旧；靠主题/自绘控件 |
| 托盘 | 无内置，需 H.NotifyIcon/Win32 | `H.NotifyIcon.Wpf`/Win32 成熟 |
| 子 HWND 承载/预览 | 互操作更麻烦 | `HwndHost`/`WindowsFormsHost`/`MediaElement` 成熟 |
| 非打包部署 | Windows App SDK bootstrapper/自包含，坑较多 | 简单，无 Windows App Runtime |
| 性能 | 现代合成，较好 | 足够 |
| 团队成本 | 中高 | 低 |
| 结论 | **1.0 选它** | 低风险备选 |

**结论**：D4 最终选 WinUI 3。接受 Windows App SDK 部署复杂度与 Win32 互操作成本，换取现代 Fluent/Mica 视觉与和 Lively 新版一致的技术方向。WPF 只作为“部署/互操作成本失控”时的降级备选，不影响 Core/Contracts/RenderHost。

#### 视频：libmpv vs mpv.exe vs Media Foundation

| 维度 | libmpv（进程内） | mpv.exe（子进程） | Media Foundation |
|---|---|---|---|
| 格式覆盖 | 极好（FFmpeg） | 极好 | 依赖系统 codec，AV1/VP9/HEVC 不稳 |
| 硬解/HDR | 好 | 好 | 好（但配置复杂） |
| 隔离 | Host 崩溃即隔离 | 双隔离（Host + mpv） | Host 内 |
| 控制粒度 | 高（属性/事件/渲染） | 中（IPC/窗口） | 中 |
| 许可 | GPLv2+，与 GPL-3.0 兼容 | GPL，边界清晰 | Windows 自带 |
| 实现成本 | 中（C API + `wid`） | 中（进程/窗口/IPC） | 高（SourceReader/IMF） |
| 结论 | **首选（GPL-3.0 兼容）** | 强隔离/调试兜底 | 缩略图/探测/极端兼容兜底 |

**建议**：`IVideoEngine` 抽象；v1 用 libmpv in-proc；`mpv.exe` 作为强隔离/排查兜底，不再是许可规避手段；缩略图/探测可先用 libmpv，减少 FFmpeg 直接依赖。

#### Web：WebView2 vs CEF/CefSharp

| 维度 | WebView2 | CEF |
|---|---|---|
| 包体 | 小（依赖系统 Runtime） | 大（100MB+） |
| 更新 | Edge 自动更新 | 自己跟版本 |
| 编解码 | 跟 Edge | 可定制 |
| 进程模型 | Chromium 多进程 | 多进程 |
| .NET 集成 | 官方 | CefSharp 成熟 |
| 结论 | 默认 | 只在固定 Chromium/特殊 codec/完全可控时 |

**建议**：WebView2；如果企业/展厅需要固定版本，提供 Fixed Version 分发作为可选。

#### 2D/Shader：Silk.NET vs Vortice

| 维度 | Silk.NET | Vortice.Windows |
|---|---|---|
| D3D11 绑定 | 有，生成式 | 有，成熟 |
| DXGI/SwapChain | 有 | 有，常用路径多 |
| D3DCompiler | `Silk.NET.Direct3D.Compilers` | `Vortice.D3DCompiler` |
| WIC/D2D/DWrite | 部分/需组合 | 完整 |
| COM 互操作体验 | 较底层 | 较顺 |
| 包大小 | 小 | 中 |
| 许可 | MIT | MIT |
| 结论 | 可用，预算更多互操作时间 | **已选** |

**结论**：D3 已选 Vortice.Windows。Silk.NET 只作为学习/备选，不再进入 1.0 依赖；`SceneRenderer` 仍放在接口后面，避免上层绑定具体库。

#### IPC：stdio vs 命名管道 vs gRPC

| 维度 | stdio 匿名管道 | 命名管道 | gRPC |
|---|---|---|---|
| 安全面 | 最小（无名称） | 需 ACL/名称 | 需端口/HTTP2 |
| 父死子灭 | EOF 天然 | 需心跳 | 需心跳 |
| 重连 | 不支持（1:1） | 支持 | 支持 |
| 多客户端 | 不适合 | 适合 | 适合 |
| 性能 | 足够（控制面） | 足够 | 较重 |
| 调试 | 稍难（二进制） | 中 | 好 |
| 结论 | **推荐** | 带外/未来 | 不推荐（UI↔Core 也不需要） |

**建议**：stdio + MessagePack；加 `--ipc-json` 调试开关；命名管道只在未来“Core 服务化/多 UI”时考虑。

#### 单进程编排 vs 独立 Core 服务

| 维度 | 单进程（推荐 v1） | 独立 Core 服务 |
|---|---|---|
| 复杂度 | 低 | 高（IPC/生命周期/权限） |
| UI 崩溃影响 | 壁纸停止（Job 清理） | 壁纸继续 |
| 更新 | 简单 | 需协调两进程 |
| 参考 | 常规桌面应用 | Lively core-separation/gRPC |

**建议**：v1 单进程；`Wallerpaper.Core` 保持无 UI 依赖，未来可抽成 `Wallerpaper.Core.exe`。用 `RegisterApplicationRestart` + Job 处理 UI 崩溃，不为了“UI 崩溃壁纸继续”提前引入第二套 IPC。

#### 其他

- **D3D11 vs D3D12**：D3D11。壁纸不需要 D3D12 的 GPU 并行/显存控制；D3D11 驱动覆盖/稳定性更好。D3D12 只在未来需要复杂计算/光追时评估。
- **JSON vs SQLite**：v1 JSON。库 <1000 项、查询简单；SQLite 留给“标签/搜索/历史/多用户”复杂化后。
- **MSIX vs unpackaged**：unpackaged + Velopack。需要任意子进程、任意库路径、开机启动，且不想被包身份/启动任务模型限制；MSIX 只用于商店版本。
- **原生场景 vs Web**：优先 Web。three.js/PixiJS 的内容生态/作者成本远低于自有场景格式；原生场景只做“低开销/Shader 差异化”，不要指望它承载大部分内容。

### 4.3 推荐最终栈

| 层 | 选择 | 版本/说明 |
|---|---|---|
| 语言/运行时 | C# / .NET 10 LTS | `net10.0-windows10.0.19041.0`；支持到 2028-11 |
| UI | WinUI 3 + Windows App SDK | `UseWinUI=true`；`WindowsPackageType=None`；`WindowsAppSDKSelfContained=true`（或安装器部署 Runtime） |
| MVVM | CommunityToolkit.Mvvm | 8.x |
| 托盘 | H.NotifyIcon.WinUI | 2.4.1（net10；或 Win32 `Shell_NotifyIcon` 兜底） |
| 视频 | libmpv `libmpv-2.dll` | mpv 官方/自建构建；`wid` 嵌入；GPLv2+ 与 GPL-3.0 兼容，附源码/许可 |
| Web | Microsoft.Web.WebView2 | 当前稳定；Evergreen Runtime 检测 |
| 场景 | Vortice.Windows（首选） | `Vortice.Direct3D11`/`DXGI`/`D3DCompiler`/`WIC` 3.8.3 |
| 场景（不选） | Silk.NET | 仅备选/学习；1.0 不引入 |
| IPC | MessagePack-CSharp | 4B length + 1B ver/type + 8B requestId |
| 日志 | Serilog 或 NLog | JSON lines；滚动；每进程 |
| 配置 | System.Text.Json | 原子写 + schemaVersion |
| 安装/更新 | Velopack | 1.2.161+；per-user；代码签名 |
| 测试 | xUnit + FlaUI | 单元/集成/UI；自托管 GPU runner |
| 崩溃 | MiniDumpWriteDump + WER | 本地转储；支持包导出 |
| 诊断 | ETW/EventSource + 自建 overlay | 可选 PDH/GPUView |

### 4.4 明确不做的事

1. **不做应用型壁纸（exe）**。Lively 支持，但这是任意代码执行；1.0 不支持。
2. **可在 GPL-3.0 下复用 Lively 代码**，但必须保留版权头、LICENSE、来源 commit；不要复制来源不明的代码。新增修改同样按 GPL-3.0 发布。
3. **不注入 DLL、不装全局钩子、不 `DebugActiveProcess` 挂起浏览器**。Lively 的 CEF 挂起技巧是反模式，见 5.6。
4. **不开 localhost HTTP/gRPC 服务**给渲染进程；stdio 足够。
5. **不通过 IPC 传帧**；帧直接进各自 HWND/GPU。
6. **不承诺 `IDesktopWallpaper` 能播放动态内容**；它只用于静态快照。
7. **不做 Wallpaper Engine `.pkg` 导入**；只支持通用视频/图片/HTML/自有场景。
8. **不要求管理员权限**；安装/运行都是 per-user。
9. **不把 `pause` 当作“释放显存”**；释放必须卸载/杀进程/Trim。
10. **不为了“多屏同步”牺牲独立崩溃域**；duplicate 模式单独设计。

### 4.5 已确认的 5 个决策

| # | 决策 | 结论 | 说明 |
|---|---|---|---|
| D1 | 许可证 | **GPL-3.0** | 可复用 Lively；保留版权/来源；libmpv/FFmpeg 做 GPL 兼容与源码分发 |
| D2 | 运行时 | **.NET 10 LTS** | 支持到 2028-11；不再考虑 .NET 8 |
| D3 | 场景渲染库 | **Vortice.Windows** | D3D11/DXGI/D3DCompiler/WIC 一套；Silk.NET 只作备选 |
| D4 | UI | **WinUI 3** | Fluent/Mica；接受 Windows App SDK 与 Win32 互操作成本 |
| D5 | 1.0 范围 | **视频 + Web + 场景** | 场景在 Phase 3；若极端工期压力，MVP 可先发视频+Web |

### 4.6 与 Lively 的关键差异（有意为之）

| 主题 | Lively | Wallerpaper | 原因 |
|---|---|---|---|
| UI/Core | WPF core + WinUI UI + gRPC | 单进程 WinUI 3 编排 | 降低 v1 复杂度；Core 保持可抽出 |
| 视频 | 外部 `mpv.exe` + 命名管道 | libmpv in-proc + `wid` | 满足 libmpv 要求；少一层进程/窗口发现 |
| Web 宿主 | WinForms + WebView2 控件 | WinForms 消息循环 + raw controller | 避免反射私有 controller；控制 DPI/安全 |
| IPC | stdio JSON lines / mpv 命名管道 | stdio MessagePack | 类型安全/性能/统一；仍保留 JSON 调试 |
| 暂停 | `pause` / CEF debugger 挂起 | TrySuspendAsync / 分级释放 / Job | 不用调试器；真正释放显存 |
| 显存 | 主要靠暂停 | 预算 + 分级 + 终止 + 静态兜底 | 游戏场景需要彻底释放 |
| 全屏 | foreground/all/grid/gamemode 可选 | 组合信号 + 每屏 + 去抖 + 应用规则 | 降低误判/漏判 |
| 输入 | RawInput + PostMessage 到 Chromium 内部窗口 | RawInput + JS 光标桥/交互模式 | 不依赖内部窗口类；更安全 |
| 静态兜底 | 可选 `DesktopAutoWallpaper` | 一等公民 | 释放/崩溃/Explorer 重启不黑屏 |
| Watchdog | 独立 `Lively.Watchdog` 进程 | Job Object + 心跳 + 退避 | 少一个进程；父死子灭 |
| 应用壁纸 | 支持 | 不支持 | 安全 |
| 场景 | 支持 Godot/Unity/ShaderToy 等 | 自有 HLSL 场景 v1 | 可控/低开销；Web 承载大部分内容 |
| 许可 | GPL-3.0 | GPL-3.0（已确认） | 双方一致；可复用 Lively，保留版权/来源 |

> 这些差异不是“Lively 做得不对”，而是新项目在“更严格的进程隔离/显存释放/安全默认值”下的不同权衡。Lively 在兼容性/功能广度上仍是标杆。

---

## 5. Lively 源码对照表

> 基线：`lively-community/lively`，分支 `core-separation`，commit `25a0a5f4ed1988ff42117d3a17feb321062237fe`。  
> 链接均指向该 commit，避免默认分支漂移。Lively 与本项目均为 GPL-3.0；可按 GPL 复用代码，但必须保留版权/许可/来源。下表同时标注我们“采用/差异”，便于审计复用范围。

### 5.1 Shell / 桌面挂靠

| Shell 操作 | Lively 源码位置 | Lively 行为 | 我们的采用/差异 |
|---|---|---|---|
| 让 Explorer 生成 WorkerW | [`WinDesktopCore.SetupDesktopLayer()`](https://github.com/lively-community/lively/blob/25a0a5f4ed1988ff42117d3a17feb321062237fe/src/Lively/Lively/Core/WinDesktopCore.cs) | `SendMessageTimeout(progman, 0x052C, 0xD, 0x1, SMTO_NORMAL, 1000)` | 采用；标注未文档化；追加 `0,0`/已有 WorkerW 兜底 |
| 找 DefView + 下一个 WorkerW | 同上 | `EnumWindows` + `FindWindowEx(top, "SHELLDLL_DefView")`，再 `FindWindowEx(0, top, "WorkerW")` | 采用；增加 class/rect/可见性校验；多 WorkerW 选择 |
| Win11 raised desktop 判定 | 同上 | `Progman` 有 `WS_EX_NOREDIRECTIONBITMAP` → raised；`FindWindowEx(progman, "WorkerW")` | 采用；额外检查 DefView `WS_EX_LAYERED`；失败回退 |
| 每屏挂靠 + 坐标 | [`TrySetWallpaperPerScreen()`](https://github.com/lively-community/lively/blob/25a0a5f4ed1988ff42117d3a17feb321062237fe/src/Lively/Lively/Core/WinDesktopCore.cs) | 先按屏幕坐标 `SetWindowPos`，`MapWindowPoints(hwnd, workerW)`，`SetParent`，再按父坐标定位；经典路径未显式改 `WS_CHILD` | 采用；按 MSDN 建议显式清 `WS_POPUP`/设 `WS_CHILD`，Phase 0 对比 Lively 的“不改样式”行为；改为 Host 进程内执行（主进程只给 WorkerW/DefView/rect） |
| raised 挂靠样式/z-order | [`TryAttachToDesktop()` / `EnsureWorkerWZOrder()`](https://github.com/lively-community/lively/blob/25a0a5f4ed1988ff42117d3a17feb321062237fe/src/Lively/Lively/Core/WinDesktopCore.cs) | `WS_CHILD` + `SetWindowTransparency(255)` + `SetParent(progman)` + `SetWindowPos(defView)`；WorkerW 压底 | 采用；`WS_EX_LAYERED` 与 D3D11 flip 的兼容性 Phase 0 验证 |
| Explorer 重启 | [`WndProc_TaskbarCreated()`](https://github.com/lively-community/lively/blob/25a0a5f4ed1988ff42117d3a17feb321062237fe/src/Lively/Lively/Core/WinDesktopCore.cs) | `TaskbarCreated` + Explorer PID 比对 + `ResetWallpaperAsync()`；短时间内多次重启时提示（阈值可配置） | 采用；增加静态快照、指数退避、全屏时延迟恢复 |
| WorkerW 销毁监听 | [`WorkerWHook_EventReceived()`](https://github.com/lively-community/lively/blob/25a0a5f4ed1988ff42117d3a17feb321062237fe/src/Lively/Lively/Core/WinDesktopCore.cs) | `EVENT_OBJECT_DESTROY` hook；raised 模式重排，否则重启 | 采用；v1 统一重启 Host；Phase 4 优化重挂 |
| 解锁后校验 | `SystemEvents_SessionSwitch()` 同上 | 解锁后 `IsWindow(WorkerW)`；无效/崩溃则 reset | 采用；WTS 通知 + 1s 延迟 |
| 静态壁纸 | [`SetDesktopPictureOrLockscreen()`](https://github.com/lively-community/lively/blob/25a0a5f4ed1988ff42117d3a17feb321062237fe/src/Lively/Lively/Core/WinDesktopCore.cs) / [`DesktopUtil.cs`](https://github.com/lively-community/lively/blob/25a0a5f4ed1988ff42117d3a17feb321062237fe/src/Lively/Lively.Common/Helpers/Shell/DesktopUtil.cs) | `IDesktopWallpaper` 设置截图；`SPI_SETDESKWALLPAPER` 兜底；图标显隐 | 采用 `IDesktopWallpaper`；静态快照做一等公民；保存/恢复原壁纸 |
| 桌面图标可见性 | `DesktopUtil.GetDesktopIconVisibility()` | `SHGetSetSettings` + `WM_COMMAND 0x7402` | 不主动改；仅用户显式选项时提供 |

### 5.2 暂停 / 全屏 / 系统状态

| 操作 | Lively 源码位置 | Lively 行为 | 我们的采用/差异 |
|---|---|---|---|
| 暂停策略 | [`Playback.Timer_Tick()`](https://github.com/lively-community/lively/blob/25a0a5f4ed1988ff42117d3a17feb321062237fe/src/Lively/Lively/Core/Suspend/Playback.cs) | 定时器；foreground/all/grid/gamemode 四种算法 | 采用组合信号；默认 foreground+独占；all/grid 可选 |
| 独占全屏 | [`SystemIdleUtil.IsExclusiveFullScreenAppRunning()`](https://github.com/lively-community/lively/blob/25a0a5f4ed1988ff42117d3a17feb321062237fe/src/Lively/Lively/Helpers/SystemIdleUtil.cs) | `SHQueryUserNotificationState` 仅 `QUNS_RUNNING_D3D_FULL_SCREEN` 为 true | 采用；`BUSY/PRESENTATION` 不直接判全屏；结合覆盖 |
| 无边框全屏 | [`WindowUtil.IsDisplayCoveredByAnyWindow()` / `GetVisibleTopLevelWindows()`](https://github.com/lively-community/lively/blob/25a0a5f4ed1988ff42117d3a17feb321062237fe/src/Lively/Lively.Common/Helpers/WindowUtil.cs) | 可见顶层窗口 + `rcWork` 覆盖率；排除 cloaked/tool/透明/无标题 | 采用；改用 `rcMonitor` 全屏覆盖；不要求标题非空；`DWMWA_EXTENDED_FRAME_BOUNDS` |
| 网格覆盖 | `WindowUtil.IsDisplayCoveredByWindowGrid()` | tile 大小/覆盖率阈值均可配置（用户设置） | 作为可选算法；默认不用，成本高 |
| 暂停原因 | `Playback.IsPauseDueToSystemState()` | 屏保/锁屏/RDP/电池/省电 | 采用；改为原因集合 + 更细的电源/会话通知 |
| 每屏暂停 | `Playback.EvaluatePlaybackByVisibleWindow()` | `DisplayPause.perdisplay/all`；span/duplicate 特殊处理 | 采用；per 默认，all 可选 |
| 应用规则 | `Playback.IsPauseRuleApp()` | 进程名规则；`pause`/`ignore`（`kill` 在源码中标记为未支持） | 采用 `pause`/`ignore`；不做 kill |

### 5.3 DPI / 显示

| 操作 | Lively 源码位置 | Lively 行为 | 我们的采用/差异 |
|---|---|---|---|
| DPI 获取 | [`DpiUtil.TryGetDisplayScale()` / `TryGetWindowScale()`](https://github.com/lively-community/lively/blob/25a0a5f4ed1988ff42117d3a17feb321062237fe/src/Lively/Lively.Common/Helpers/DpiUtil.cs) | 检查 PerMonitor；`GetDpiForMonitor(MDT_EFFECTIVE_DPI)` / `GetDpiForWindow` | 采用；优先 `GetDpiForWindow`（临时 top-level 窗口）；`GetDpiForMonitor` 仅 fallback |
| 显示器枚举 | [`DisplayManager.RefreshDisplayMonitorList()`](https://github.com/lively-community/lively/blob/25a0a5f4ed1988ff42117d3a17feb321062237fe/src/Lively/Lively/Core/Display/DisplayManager.cs) | `EnumDisplayMonitors`/`GetMonitorInfo`/`EnumDisplayDevices`；`WM_DISPLAYCHANGE` | 采用；增加 `QueryDisplayConfig` 稳定 ID/EDID；`RegisterDeviceNotification` |
| 稳定 ID | `DisplayManager.GetDefaultDisplayDeviceId()` | `DeviceID`，否则 bounds hash | 采用 `monitorDevicePath` + EDID + geometry fallback；`\\.\DISPLAYn` 仅会话内 |
| 虚拟屏 | `DisplayManager.VirtualScreenBounds` | `SM_XVIRTUALSCREEN` 等 | 采用；span 模式用 |

### 5.4 输入

| 操作 | Lively 源码位置 | Lively 行为 | 我们的采用/差异 |
|---|---|---|---|
| 全局输入 | [`RawInputMsgWindow.Window_SourceInitialized()`](https://github.com/lively-community/lively/blob/25a0a5f4ed1988ff42117d3a17feb321062237fe/src/Lively/Lively/Views/WindowMsg/RawInputMsgWindow.xaml.cs) | `RawInputDevice.RegisterDevice(Mouse/Keyboard, ExInputSink, hwnd)` | 采用 `RIDEV_INPUTSINK`；默认只注册鼠标，键盘仅交互模式开启；不用全局钩子 |
| 鼠标转发 | [`InputUtil.ForwardMessageMouse()`](https://github.com/lively-community/lively/blob/25a0a5f4ed1988ff42117d3a17feb321062237fe/src/Lively/Lively.Common/Helpers/InputUtil.cs) / `WinDesktopCore.ForwardMouseToWallpapers()` | `PostMessage(WM_MOUSEMOVE/LBUTTONDOWN/...)` 到 `InputHandle` | 仅兼容模式；默认 JS 光标桥/交互模式；不依赖 Chromium 内部窗口 |
| 坐标转换 | `InputUtil.ToMouseDisplayLocal()` / `ToMouseSpanLocal()` | 全局→屏幕局部 | 采用；物理像素 + DPI 校验 |
| 桌面判定 | `WinDesktopCore.IsDesktop()` | 前台 == 原 WorkerW/Progman | 采用；加 DefView/壳窗口判断 |
| WebView2 输入句柄 | [`WebWebView2.InputHandle`](https://github.com/lively-community/lively/blob/25a0a5f4ed1988ff42117d3a17feb321062237fe/src/Lively/Lively/Core/Wallpapers/WebWebView2.cs) | `FindWindowEx(webView.Handle, "Chrome_WidgetWin_0")` → `Chrome_WidgetWin_1` | 不作为主路径；仅兼容/诊断；内部类不是 API |

### 5.5 播放器 / IPC

| 操作 | Lively 源码位置 | Lively 行为 | 我们的采用/差异 |
|---|---|---|---|
| mpv 启动 | [`VideoMpvPlayer` 构造函数](https://github.com/lively-community/lively/blob/25a0a5f4ed1988ff42117d3a17feb321062237fe/src/Lively/Lively/Core/Wallpapers/VideoMpvPlayer.cs) | 外部 `mpv.exe`；`--force-window`/`--geometry=-9999:0`/`--window-minimized`/`--input-ipc-server` | 改为 libmpv in-proc + `wid`；保留相同 mpv 选项语义 |
| mpv 暂停 | `VideoMpvPlayer.Pause()` | `set_property pause true` | 采用；追加 `stop`/`vid=no`/杀 Host 分级 |
| mpv 音量/静音 | `SetVolume()` / `SetMute()` | `volume`；`aid=no/1` | 采用；音频所有者策略 |
| mpv 窗口 | `ShowAsync()` / `WindowUtil.BorderlessWinStyle()` / `RemoveWindowFromTaskbar()` | 找进程窗口；去边框；TOOLWINDOW/NOACTIVATE | 自己创建窗口；不用找进程窗口 |
| WebView2 启动 | [`Lively.Player.WebView2/Form1.cs`](https://github.com/lively-community/lively/blob/25a0a5f4ed1988ff42117d3a17feb321062237fe/src/Lively/Lively.Player.WebView2/Form1.cs) | WinForms；`--autoplay-policy`；UDF；`WS_EX_TOOLWINDOW|NOACTIVATE` | 采用 WinForms 消息循环 + raw controller；安全默认值更严 |
| WebView2 本地映射 | [`CoreWebView2Extensions.NavigateToLocalPath()`](https://github.com/lively-community/lively/blob/25a0a5f4ed1988ff42117d3a17feb321062237fe/src/Lively/Lively.Player.WebView2/Extensions/WebView2/CoreWebView2Extensions.cs) | `SetVirtualHostNameToFolderMapping` + `https://host/file` | 采用；增加导航白名单/离线阻断 |
| WebView2 暂停 | `Form1.HandleSuspend()` / `HandleResume()` | `DebugActiveProcess` 挂起 CEF D3D 渲染子进程；`TryPauseMedia` JS | **不采用调试器方案**；用 `IsVisible=false` + `TrySuspendAsync`；JS 媒体暂停保留 |
| WebView2 IPC | [`WebWebView2.SendMessage()`](https://github.com/lively-community/lively/blob/25a0a5f4ed1988ff42117d3a17feb321062237fe/src/Lively/Lively/Core/Wallpapers/WebWebView2.cs) | `RedirectStandardInput/Output` + JSON lines | 改为长度前缀 MessagePack；stdin/stdout 语义一致 |
| 播放器监控 | [`WatchdogProcess`](https://github.com/lively-community/lively/blob/25a0a5f4ed1988ff42117d3a17feb321062237fe/src/Lively/Lively/Core/Watchdog/WatchdogProcess.cs) | 独立 `Lively.Watchdog` 进程，stdin 发 ADD/RMV/CLR | 改为 Job Object + 心跳 + 父进程 EOF；少一个进程 |
| UI/Core IPC | [`Lively.Grpc.Common/Proto`](https://github.com/lively-community/lively/tree/25a0a5f4ed1988ff42117d3a17feb321062237fe/src/Lively/Lively.Grpc.Common/Proto) / `Lively/RPC` | gRPC 服务（commands/display/settings/update/wallpaper） | v1 单进程，不需要；未来 Core 服务化可参考 |
| 托盘 | [`Systray.cs`](https://github.com/lively-community/lively/blob/25a0a5f4ed1988ff42117d3a17feb321062237fe/src/Lively/Lively/Services/Systray.cs) | 自绘托盘/菜单；TaskbarCreated 重加 | 用 H.NotifyIcon.WinUI 或 Win32 `Shell_NotifyIcon`；必须处理 TaskbarCreated |
| 开机启动 | [`WindowsStartup.cs`](https://github.com/lively-community/lively/blob/25a0a5f4ed1988ff42117d3a17feb321062237fe/src/Lively/Lively/Helpers/WindowsStartup.cs) | 负责开机启动（具体实现见源码） | 采用 HKCU Run；`--autostart`；延迟等 Explorer |

### 5.6 我们刻意不采用的三件事

1. **`DebugActiveProcess` 挂起 WebView2 的 CEF 渲染子进程**  
   Lively `Form1.SuspendRenderingSubProcess()` 用调试器附加来冻结 GPU 渲染。问题：依赖调试权限/安全策略；杀软/EDR 高度敏感；[`DebugSetProcessKillOnExit`](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-debugsetprocesskillonexit) 默认 TRUE（按线程生效），调试线程退出可能终止被附加的进程；必须同线程 detach；不是 WebView2 官方支持路径。我们用 `IsVisible=false` + `TrySuspendAsync`，彻底释放则杀 Host。

2. **`PostMessage` 到 `Chrome_WidgetWin_1` 注入鼠标**  
   `Chrome_WidgetWin_*` 是 Chromium 内部窗口类，不是 API；不同版本可能变。我们默认用 `PostWebMessageAsJson` + JS shim 做光标效果；点击交互用热键临时退出穿透；`CoreWebView2CompositionController.SendMouseInput` 作为官方注入路径在 Phase 2 spike。

3. **独立 Watchdog 进程**  
   Lively 的 Watchdog 解决“Core 死了播放器还在”的问题；我们用 Job Object `KILL_ON_JOB_CLOSE` + stdio EOF + 心跳，少一个进程和一套 IPC。若未来 Core 服务化，再评估独立 Watchdog。

---

## 6. 附录

### 6.1 已验证的 Win32 / COM / WebView2 / DXGI API

> 规则：每个 P/Invoke 声明必须带 MSDN 链接或 Lively 源码链接；没有来源的 API 不进代码。  
> 以下均为公开文档 API（除 6.2 单独列出）。

| API | 用途 | 文档 |
|---|---|---|
| `SetParent` | 把 Host 窗口挂到 WorkerW/Progman；挂靠前按 MSDN 切换 `WS_CHILD`/`WS_POPUP` | [MSDN](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setparent) |
| `MapWindowPoints` | 屏幕坐标 → 父客户区坐标 | [MSDN](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-mapwindowpoints) |
| `SetWindowPos` | 位置/尺寸/z-order | [MSDN](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos) |
| `FindWindow` / `FindWindowEx` | 找 Progman/WorkerW/DefView | [MSDN](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-findwindowexw) |
| `EnumWindows` | 枚举顶层窗口 | [MSDN](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumwindows) |
| `SendMessageTimeout` | 发 `0x052C`，避免卡死 | [MSDN](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendmessagetimeoutw) |
| `GetWindowLongPtr` / `SetWindowLongPtr` | `WS_EX_*`/`WS_CHILD` 样式 | [MSDN](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindowlongptrw) |
| `ShowWindow` | 显示/隐藏，不激活 | [MSDN](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-showwindow) |
| `DwmGetWindowAttribute` | `DWMWA_CLOAKED`、扩展边框 | [MSDN](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmgetwindowattribute) |
| `SHQueryUserNotificationState` | 独占全屏检测 | [MSDN](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shqueryusernotificationstate) |
| `GetLastInputInfo` | 空闲检测（屏保/离开） | [MSDN](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getlastinputinfo) |
| `RegisterRawInputDevices` / `GetRawInputData` | 全局鼠标/键盘（非 hook） | [MSDN](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerrawinputdevices) |
| `RegisterWindowMessage` | `TaskbarCreated` | [MSDN](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerwindowmessagew) |
| `SetWinEventHook` | WorkerW `EVENT_OBJECT_DESTROY` | [MSDN](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwineventhook) |
| `GetDpiForWindow` | 窗口 DPI（优先） | [MSDN](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getdpiforwindow) |
| `SetProcessDpiAwarenessContext` | PerMonitorV2 | [MSDN](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setprocessdpiawarenesscontext) |
| `SetThreadDpiAwarenessContext` / `SetThreadDpiHostingBehavior` | 跨进程挂靠 DPI | [MSDN](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setthreaddpihostingbehavior) |
| `EnumDisplayMonitors` / `GetMonitorInfo` | 显示器枚举/矩形 | [MSDN](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumdisplaymonitors) |
| `MonitorFromWindow` / `MonitorFromPoint` | HMONITOR | [MSDN](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-monitorfromwindow) |
| `QueryDisplayConfig` / `DisplayConfigGetDeviceInfo` | 稳定显示器 ID/EDID | [MSDN](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-querydisplayconfig) |
| `CreateJobObject` / `SetInformationJobObject` / `AssignProcessToJobObject` | 子进程生命周期 | [MSDN](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createjobobjectw) |
| `WTSRegisterSessionNotification` | 锁屏/解锁/RDP | [MSDN](https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/nf-wtsapi32-wtsregistersessionnotification) |
| `RegisterPowerSettingNotification` | 显示器关闭/电池 | [MSDN](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerpowersettingnotification) |
| `RegisterDeviceNotification` | 显示器到达/移除 | [MSDN](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerdevicenotificationw) |
| `Shell_NotifyIcon` | 托盘图标/菜单 | [MSDN](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shell_notifyiconw) |
| `RegisterHotKey` | 全局热键 | [MSDN](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey) |
| `IDesktopWallpaper` | 每显示器静态快照/恢复 | [MSDN](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-idesktopwallpaper) |
| `D3D11CreateDevice` | D3D11 设备 | [MSDN](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-d3d11createdevice) |
| `IDXGIFactory2::CreateSwapChainForHwnd` | 交换链 | [MSDN](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgifactory2-createswapchainforhwnd) |
| `IDXGIDevice3::Trim` | 释放图形内存 | [MSDN](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_3/nf-dxgi1_3-idxgidevice3-trim) |
| `IDXGIAdapter3::QueryVideoMemoryInfo` | 显存预算/用量 | [MSDN](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_4/nf-dxgi1_4-idxgiadapter3-queryvideomemoryinfo) |
| `RegisterVideoMemoryBudgetChangeNotificationEvent` | 预算变化通知 | [MSDN](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_4/nf-dxgi1_4-idxgiadapter3-registervideomemorybudgetchangenotificationevent) |
| `D3DCompile` | 运行时 HLSL 编译 | [MSDN](https://learn.microsoft.com/en-us/windows/win32/api/d3dcompiler/nf-d3dcompiler-d3dcompile) |
| `MiniDumpWriteDump` | 崩溃转储 | [MSDN](https://learn.microsoft.com/en-us/windows/win32/api/dbghelp/nf-dbghelp-minidumpwritedump) |
| `GetGuiResources` / `GetProcessHandleCount` | 泄漏测试 | [MSDN](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getguiresources) |
| `CoreWebView2.TrySuspendAsync` | Web 暂停/省内存 | [MSDN](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2.trysuspendasync) |
| `CoreWebView2.MemoryUsageTargetLevel` | 低内存目标（不混用） | [MSDN](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2.memoryusagetargetlevel) |
| `CoreWebView2CompositionController.SendMouseInput` | 官方鼠标注入（composition） | [MSDN](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2compositioncontroller.sendmouseinput) |
| `CoreWebView2Controller.RasterizationScale` | WebView2 DPI | [MSDN](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2controller.rasterizationscale) |
| `CoreWebView2.SetVirtualHostNameToFolderMapping` | 本地内容虚拟主机 | [MSDN](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2.setvirtualhostnametofoldermapping) |
| `CoreWebView2.CapturePreviewAsync` | 快照 | [MSDN](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2.capturepreviewasync) |
| `CoreWebView2.ProcessFailed` | 进程崩溃 | [MSDN](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2.processfailed) |

### 6.2 未文档化 / 事实标准 / 易变行为

| 行为 | 状态 | 验证来源 | 我们的处理 |
|---|---|---|---|
| `SendMessageTimeout(progman, 0x052C, 0xD, 0x1, ...)` | **未文档化** 的 Progman 消息 | Lively `SetupDesktopLayer()` | 尝试 + 兜底；不承诺；不写成系统 API |
| 找 `SHELLDLL_DefView` 的兄弟 `WorkerW` | Explorer 内部窗口层级 | Lively 同上 | 能力探测；校验 class/rect；多策略 |
| `WS_EX_NOREDIRECTIONBITMAP` 判定 raised desktop | 样式本身有文档；Explorer 的用法无 API 合同 | Lively 同上 + MSDN 样式文档 | 检测 + `WS_EX_LAYERED` 辅助；逐 build 测试 |
| `Chrome_WidgetWin_0/1`、`Intermediate D3D Window` | Chromium 内部窗口类 | Lively `WebWebView2`/`CoreWebView2Extensions` | 仅兼容/诊断；不作为主路径 |
| `DebugActiveProcess` 挂起 CEF | 有文档，但不是 WebView2 支持用法 | Lively `Form1.HandleSuspend()` | **不使用**；见 5.6 |
| `WS_EX_LAYERED` + D3D11 flip model | 行为取决于驱动/DWM | 需 Phase 0 实测 | spike；失败用 DISCARD/DComp |
| `SHQueryUserNotificationState` 的 `QUNS_BUSY` 语义 | 有文档，但“是否全屏”因场景而异 | Lively 只认 `RUNNING_D3D_FULL_SCREEN` | 不单独作为全屏依据 |
| 第三方 Explorer 修改器（ExplorerPatcher/StartAllBack） | 无合同 | 实测 | 兼容矩阵 + 提示；不主动对抗 |

### 6.3 不要声称存在的 API / 能力

- ❌ `SetDynamicWallpaper` / `SetLiveWallpaper` / `IWallpaperEngine` —— 不存在。
- ❌ `WorkerW` 是公开 API/扩展点 —— 不是；是 Explorer 内部窗口类。
- ❌ `IDesktopWallpaper` 能播放视频/网页 —— 不能；只设静态图。
- ❌ `SystemParametersInfo(SPI_SETDESKWALLPAPER)` 能播放动态内容 —— 不能。
- ❌ WebView2 窗口版有官方“注入鼠标”API —— 没有；`SendMouseInput` 属于 `CoreWebView2CompositionController`（composition 宿主）。
- ❌ `TrySuspendAsync` 可以在 `IsVisible=true` 时调用 —— 会抛 `ERROR_INVALID_STATE`；必须先 `IsVisible=false`。
- ❌ `libmpv` 的 `wid` 是“桌面壁纸 API” —— 它只是把视频输出嵌到一个 HWND。
- ❌ 用 `pause=true` 就能释放视频解码显存 —— 不能；要 `stop`/卸载/杀进程。
- ❌ 改 `SHELLDLL_DefView` 的样式/消息是安全做法 —— 不要碰；会破坏桌面交互。

### 6.4 参考资料

**Lively**

- 仓库：<https://github.com/lively-community/lively>
- 本次基线 commit：<https://github.com/lively-community/lively/tree/25a0a5f4ed1988ff42117d3a17feb321062237fe>
- 许可证（GPL-3.0）：<https://github.com/lively-community/lively/blob/25a0a5f4ed1988ff42117d3a17feb321062237fe/LICENSE>

**Microsoft**

- .NET 支持策略：<https://learn.microsoft.com/en-us/dotnet/core/releases-and-support>
- Windows App SDK 非打包部署：<https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/deploy-unpackaged-apps>
- WinUI 3：<https://learn.microsoft.com/en-us/windows/apps/winui/winui3/>
- WebView2 文档：<https://learn.microsoft.com/en-us/microsoft-edge/webview2/>
- `TrySuspendAsync`：<https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2.trysuspendasync>
- `SendMouseInput`：<https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2compositioncontroller.sendmouseinput>
- `IDXGIDevice3::Trim`：<https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_3/nf-dxgi1_3-idxgidevice3-trim>
- `IDXGIAdapter3::QueryVideoMemoryInfo`：<https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_4/nf-dxgi1_4-idxgiadapter3-queryvideomemoryinfo>
- `IDesktopWallpaper`：<https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-idesktopwallpaper>
- `SHQueryUserNotificationState`：<https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shqueryusernotificationstate>
- `SetWinEventHook`：<https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwineventhook>

**库/工具**

- Vortice.Windows：<https://github.com/amerkoleci/Vortice.Windows>（NuGet `Vortice.Direct3D11` 3.8.3）
- Silk.NET：<https://github.com/dotnet/Silk.NET>（NuGet `Silk.NET.Direct3D.Compilers` 2.23.0）
- H.NotifyIcon：<https://github.com/HavenDV/H.NotifyIcon>（NuGet `H.NotifyIcon.WinUI` 2.4.1）
- Velopack：<https://github.com/velopack/velopack>（NuGet `Velopack` 1.2.161）
- mpv 手册：<https://mpv.io/manual/master/>（`--wid`、`--input-ipc-server`、`--hwdec`）
- WebView2 Feedback（输入/内存限制）：<https://github.com/MicrosoftEdge/WebView2Feedback>

### 6.5 术语表

| 术语 | 含义 |
|---|---|
| WorkerW | Explorer 桌面窗口层级中的 Worker 窗口；经典路径下用来承载壁纸 |
| Progman | “Program Manager” 顶层窗口；Win11 raised desktop 下作为壁纸父窗口 |
| SHELLDLL_DefView | 桌面图标/右键/框选所在的 Shell View 子窗口 |
| raised desktop | Win11 新桌面合成方式：Progman `WS_EX_NOREDIRECTIONBITMAP`，DefView `WS_EX_LAYERED` |
| Host | `Wallerpaper.RenderHost.exe`，一个壁纸实例 × 显示器 |
| 壁纸实例 | `(WallpaperId, MonitorId)` 的唯一组合；span/duplicate 例外 |
| 静态兜底 | 每个显示器一张与当前壁纸匹配的静态图，Host 不在时显示 |
| PauseReason | 暂停原因集合；全部清零才恢复 |
| ReleaseLevel | 暂停后的释放级别：RenderOnly/Media/Unload/Terminated |
| 经典路径 | 挂到含 DefView 的 WorkerW 后面的 WorkerW |
| raised 路径 | 挂到 Progman 下、DefView 下方，带 `WS_EX_LAYERED` |
| UDF | WebView2 User Data Folder |
| DXBC | Direct3D 编译后的着色器字节码 |
| TDR | Timeout Detection and Recovery；GPU 挂起后系统重置显示驱动 |

---

## 7. 下一步（已确认，直接开工）

D1–D5 已确认：**GPL-3.0、.NET 10 LTS、Vortice.Windows、WinUI 3、视频+Web+场景**。  
接下来按 Phase 0 建 spike 分支与最小可运行工程：

1. `spike-workerw`：经典 + raised + 图标交互；
2. `spike-scene`：D3D11 layered + HLSL（Vortice）；
3. `spike-web`：raw WebView2 + DPI；
4. `spike-video`：libmpv `wid`；
5. `spike-ipc`：Job + stdio MessagePack。

Phase 0 结束出测量报告，再冻结 1.0 范围与工期。

> 本文不是“最终真理”，而是一份可执行的工程基线。所有未文档化行为都应在 Phase 0 用实机验证；所有验收数字都应在参考机上校准后再写进合同/发布标准。
