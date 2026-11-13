# WallerpaperHost：主调度器（阶段 5）

统一管理每个显示器的渲染子进程、前台全屏检测、三档性能策略、看门狗、电源策略和资源清理。

## 1. 状态机

### 主机状态

```text
                 +-----------+
                 | Starting  |
                 +-----+-----+
                       |
                       v
                 +-----------+
      +--------->|  Running  |<---------+
      |          +-----+-----+          |
      |                |                |
      |      全屏/策略=Pause            | 全屏结束
      |                v                |
      |          +-----------+          |
      +----------|  Paused   |----------+
                 +-----------+

  子进程连续崩溃 > 阈值 → Degraded（降级为无壁纸/静态壁纸占位）
  Ctrl+C / --seconds / 退出 → ShuttingDown
```

### 单显示器会话状态

```text
Stopped -> Starting -> Running <-> Paused
                    |        |
                    |        崩溃/ready 超时
                    v        v
                 Faulted -> Restarting -> Starting
                              |
                              +-> 超过重启阈值 -> Degraded
```

策略只在 **该显示器** 上执行；其他显示器继续 Running。

## 2. 全屏检测

代码：[`ForegroundWatcher.cs`](ForegroundWatcher.cs)

```csharp
var hwnd = GetForegroundWindow();
var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);

// 1. 排除不可见/最小化/DWM cloaked
// 2. 排除桌面/任务栏/WorkerW/Progman
// 3. 排除进程名/标题/窗口类
// 4. IsZoomed() 或 WS_MAXIMIZE -> 普通最大化，不算全屏
// 5. DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)
//    rect 覆盖 MonitorFromWindow 的 rcMonitor（±2px） -> 全屏
```

判断顺序：

1. `GetForegroundWindow` 为空 → none；
2. `IsWindowVisible` / `IsIconic` / DWM cloaked；
3. 桌面/Shell 类：`Progman`、`WorkerW`、`SHELLDLL_DefView`、`Shell_TrayWnd`；
4. 排除列表：
   - 进程：`Taskmgr.exe`、`SystemSettings.exe`、`ApplicationFrameHost.exe`、`SearchHost.exe`、`LockApp.exe`、`SceneWallpaperHost.exe` 等；
   - 标题：`Task Manager`、`任务管理器`、`Settings`、`设置`；
   - 窗口类：`TaskManagerWindow`、`ApplicationFrameWindow`；
5. 最大化窗口不算全屏；
6. 可见 frame 覆盖该显示器 monitor rect（容差 2px）→ 全屏；
7. **后台窗口扫描**：即使全屏窗口不是前台，只要它属于 `fullscreenScanProcesses`（默认 PotPlayer/mpv/VLC/WMP/Edge/Chrome/Firefox），也保持暂停；
8. **去抖**：全屏/恢复候选状态需连续 2 次 250ms 轮询（约 500ms）才提交，避免 alt-tab、弹窗、浏览器焦点切换造成 pause/resume 抖动；
9. **WinEvent**：`EVENT_SYSTEM_FOREGROUND`、`EVENT_OBJECT_LOCATIONCHANGE`、最小化事件只作为唤醒提示，策略仍由主循环去抖后执行。

### 为什么不用“独占全屏”API

Windows 没有可靠的公开 API 区分 exclusive fullscreen / borderless fullscreen。  
本实现把**覆盖整块显示器的无边框窗口**也视作全屏，这对壁纸暂停是正确行为；同时用 `IsZoomed` 排除普通最大化窗口。

限制：

- UWP/设置窗口可能通过 `ApplicationFrameHost` 代理，需进程/标题排除；
- 某些游戏使用多显示器全屏，只覆盖主屏时只暂停对应屏；
- 全屏切换瞬间可能有 250ms 轮询延迟。

## 3. 三档性能策略

配置：

```json
{
  "policy": "pause",              // continue | pause | stop
  "triggerOnMaximized": true      // false 可避免最大化办公窗口误触发
}
```

最大化窗口默认**不**当全屏；只有 `triggerOnMaximized=true` 时才按同一策略处理。默认样本为 `true` 以便验证最大化策略；桌面办公可改 `false`。

| 策略 | 全屏时 | 全屏退出 | 资源 |
|---|---|---|---|
| `continue` | 不处理 | 不处理 | 继续渲染 |
| `pause` | 发送 `pause`，保留进程/HWND/显存 | 发送 `resume` | 冻结画面，快速恢复 |
| `stop` | 发送 `stop`，3s 未退则 kill tree | 重新 `Start` 子进程 | 完全释放内存/显存 |

调度器只对**检测到全屏的那台显示器**执行策略：

```csharp
foreach (var device in FullscreenMonitors.Except(fullscreenThisTick))
    ReleaseFullscreen(device);

foreach (var device in fullscreenThisTick.Except(FullscreenMonitors))
    ApplyFullscreen(device);
```

## 4. 多显示器

代码：[`MonitorManager.cs`](MonitorManager.cs)

```csharp
EnumDisplayMonitors(...);
GetMonitorInfo(hMonitor, ref MONITORINFOEX);
// MONITORINFOEX.szDevice = \\.\DISPLAY1
```

每个显示器一个 `ChildSession`：

```text
DISPLAY1 -> ChildSession -> SceneWallpaperHost.exe --bounds 0 0 1920 1200
DISPLAY2 -> ChildSession -> SceneWallpaperHost.exe --bounds 1920 0 2560 1440
```

- 每个子进程创建自己的 WorkerW child window，位置/大小取自 `--bounds`；
- 共享 WorkerW 宿主代码已支持 `SetTargetRect`；
- 每 2s 比较显示器集合：
  - 移除：`Stop` + 清理会话；
  - 新增：按默认或 `monitors[device]` 配置启动；
  - 尺寸/位置变化：销毁旧会话，按新 bounds 重建。

配置示例：

```json
{
  "type": "scene",
  "wallpaperPath": "scenes/audio_gradient/scene.json",
  "videoChildExe": "...\\VideoWallpaperHost.exe",
  "webChildExe": "...\\WebWallpaperHost.exe",
  "monitors": {
    "\\\\.\\DISPLAY1": { "type": "video", "wallpaperPath": "D:\\a.mp4" },
    "\\\\.\\DISPLAY2": { "type": "web", "wallpaperPath": "D:\\wallpaper\\index.html" }
  }
}
```

Scene / Video / Web 都支持 `--bounds x y w h`：

- Scene：D3D11 swapchain 使用目标客户区；
- Video：mpv 的 WorkerW 窗口使用目标客户区；
- Web：WebView2 host/controller 使用目标客户区；

未配置的显示器使用全局 type/path。

## 5. 看门狗

代码：[`ChildSession.cs`](ChildSession.cs)、[`Program.cs`](Program.cs)

- 子进程 `Exited` 且不是主动停止 → `Faulted`；
- `UnexpectedExit` → `Restarting`；
- 1s 延迟后重启（可配置）；
- 60s 窗口内重启次数 ≥ `maxRestarts` → `Degraded`，不再重启并记录；
- `ready` 20s 超时 → kill，进入同一看门狗；
- 子进程 `stop` 是主动停止，不触发重启。

### 资源清理

```text
Ctrl+C / --seconds
  → _shuttingDown = true
  → 对每个 ChildSession: Stop(intentional=true)
       → stdin {"cmd":"stop"}
       → 3s 等待；否则 Kill(entireProcessTree:true)
  → 解除事件订阅/清空 Sessions
  → JobObject.Dispose()：JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
  → 子进程的 WorkerW 窗口随进程销毁
```

Job Object 覆盖所有子进程；即使主调度器崩溃，也会杀掉保留的进程树。

### 静态壁纸降级

`fallbackImage` 指向静态图时，看门狗超过重启阈值会：

```text
IDesktopWallpaper.GetWallpaper(monitorId)  保存原壁纸
IDesktopWallpaper.SetWallpaper(monitorId, fallbackImage)
```

只对该显示器生效；主程序退出/显示器重新加入/手动恢复时还原原壁纸。

实测：

```text
static fallback applied: ...\fallback.png
fallback restored for \\?\DISPLAY#...
```

`--test-fallback` 可在启动时立即应用，退出时恢复，用于验证 COM 路径。

## 6. 电源与帧率

代码：[`Program.cs`](Program.cs) `PollPower`

```csharp
GetSystemPowerStatus(out var status);
var ac = status.ACLineStatus == 1;
_currentFps = ac ? config.AcFps : config.BatteryFps;
```

- AC：`acFps`（默认 60）；
- 电池：`batteryFps`（默认 30）；
- 变化时对未受全屏策略抑制的会话重启子进程应用新 FPS；
- 启动时立即读取一次，避免默认 60 覆盖电池策略。

## 7. 测试记录

环境：单显示器 `\\.\DISPLAY1 0,0 1920x1200 primary`，Windows 11，Medium integrity。

### 基础启动

```text
monitors: \\.\DISPLAY1 0,0 1920x1200 primary
[\\.\DISPLAY1] child started pid=13936 bounds=0,0 1920x1200 fps=60
[\\.\DISPLAY1] ready
stats fps=60.1 gpuMs=0.1 ...
```

### 全屏暂停/恢复：真实 mp4

视频：

```text
C:\Users\legion\Videos\屏幕录制\屏幕录制 2026-10-25 182749.mp4
```

用 PotPlayer `/fullscreen` 播放并激活到前台：

```text
fullscreen: PotPlayerMini64.exe "屏幕录制 2026-10-25 182749.mp4 - PotPlayer" class=PotPlayer64
pause requested
paused
...
fullscreen cleared: DeepSeek Harness.exe "Windows动态壁纸引擎架构设计..."
resume requested
resumed
```

切回桌面（终端重新成为前台）后自动 `resume`。

> 测试过程中 Edge/WMP 曾出现未触发，原因是它们启动后没有真正获得前台焦点；显式 `SetForegroundWindow` 后检测正常。这不是检测逻辑漏触发，但说明后台启动播放器时需要确保前台激活。

### 后台全屏窗口

不把 PotPlayer 激活到前台，仅让全屏窗口存在：

```text
fullscreen window (background)
pause requested
paused
```

随后关闭播放器，恢复。

注意：后台扫描**只对 `fullscreenScanProcesses` 白名单**生效，避免终端/IDE 等 borderless 最大化窗口造成误暂停。游戏进程需加入该列表。

### Web / Video 子进程

```text
# web
type=web, child=WebWallpaperHost.exe
Attached: hwnd=... rect=(0,0,1920x1200)
web ready

# video
type=video, child=VideoWallpaperHost.exe
Attached: hwnd=... rect=(0,0,1920x1200)
```

三者统一走 `ChildSession --bounds`。

### 静态降级

```text
static fallback applied: ...\fallback.png
...
fallback restored for \\?\DISPLAY#...
```

### 最大化窗口

`scheduler.json`：

```json
"triggerOnMaximized": true
```

打开 `WindowState=Maximized` 的 WinForms 窗口：

```text
maximized: powershell.exe "Maximized Policy Test 2" class=WindowsForms10...
pause requested
paused
...
fullscreen cleared: DeepSeek Harness.exe ...
resume requested
resumed
```

### 去抖

候选全屏/恢复状态连续 2 次（约 500ms）才提交，避免 Edge/桌面切换产生的短暂 pause/resume 抖动。

### 电池模式

本机为台式/AC 环境，无法真实拔电；使用 `--force-battery` 验证策略路径：

```text
power: battery fps=30
child started ... fps=30
stats: fpsCap=30, fps=30.2
```

真实笔记本上由 `GetSystemPowerStatus` 的 `ACLineStatus` 自动切换。

### 看门狗

手动 `Stop-Process` 子进程：

```text
child exited unexpectedly code=-1
watchdog restart in 1000ms (1/3)
ready pid=9168
```

### 清理

```text
shutdown requested
stopped
cleanup complete; no child/job object remains
```

无 `SceneWallpaperHost`/`WallerpaperHost` 残留。

## 8. 兼容性与限制

1. 全屏检测是**近似**：无法可靠区分 exclusive fullscreen 与 borderless fullscreen；`triggerOnMaximized` 控制是否把最大化窗口也视为策略触发。
2. 多显示器代码已实现，但本次环境只有一台显示器，未做真实双屏/热插拔验证。
3. Scene/Video/Web 均已接入 `--bounds`；真实双屏仍需逐屏验证。
4. 静态壁纸已通过 `IDesktopWallpaper` 按屏应用/恢复；Windows 7 不支持该 COM 接口时需 fallback。
5. 电池切换路径用 `--force-battery` 模拟验证；未在真实笔记本拔电，也未做 GPU 功耗/温度策略。
6. 全屏轮询 250ms + 两次去抖，状态切换延迟约 250–500ms。
7. 混合 DPI：子进程用物理像素 bounds，Web 用 `GetDpiForWindow`；混合 DPI 多屏仍需逐屏验证。
8. 需要从 Medium integrity 运行，否则子进程 `SetParent` 可能 access denied。
9. 调度器是控制台程序，未做托盘/设置 UI。
10. 后台全屏扫描限定 `fullscreenScanProcesses`；游戏/其他播放器需加入白名单，否则只有前台检测。
11. 全屏窗口扫描/最大化策略可能把某些 borderless 桌面应用当成全屏；可用 exclude 列表或 `triggerOnMaximized=false` 规避。

## 9. 运行

```powershell
$build = "$env:USERPROFILE\WallerpaperBuild"
dotnet build "samples\WallerpaperHost\WallerpaperHost.csproj" -c Release -o "$build\host"

# 编辑 scheduler.json：childExe / scene / policy / monitors
& "$build\host\WallerpaperHost.exe" --config "$build\host\scheduler.json"
```

WinUI 3 Shell 通过 stdin 控制：

```text
pause-all
resume-all
status
quit
```

实时属性（JSON 行）：

```json
{"cmd":"volume","device":"\\\\.\\DISPLAY1","value":25}
{"cmd":"speed","device":"\\\\.\\DISPLAY1","value":0.5}
{"cmd":"shader","device":"\\\\.\\DISPLAY1","value":1.5}
```

Shell 端见 [`samples/WallerpaperShell/Services/HostBridge.cs`](../WallerpaperShell/Services/HostBridge.cs)。

测试自动退出：

```powershell
... --seconds 15
```
