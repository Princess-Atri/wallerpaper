# WebWallpaperHost（阶段 3：WebView2 Web 壁纸）

## 结构

```text
WebWallpaperParent.exe
  ├─ WASAPI loopback（NAudio）→ 32 段频谱 → stdio IPC
  └─ 启动 → WebWallpaperHost.exe（独立子进程）
       ├─ 共享 WorkerWWindow（与视频子进程共用）
       ├─ CoreWebView2Environment + raw CoreWebView2Controller
       ├─ worker 窗口（WS_EX_TRANSPARENT/NOACTIVATE）
       ├─ 鼠标轮询 → PostWebMessageAsJson → JS bridge
       ├─ 频谱命令 → JS bridge
       └─ Dispose → controller.Close → 删除独立 UserDataFolder → 退出
```

共享 WorkerW：[`../Shared/WorkerWRuntime/WorkerWWindow.cs`](../Shared/WorkerWRuntime/WorkerWWindow.cs)  
视频子进程也已改为引用该类库，不再各自复制一份。

## 构建

```powershell
dotnet build "samples\WebWallpaperHost\WebWallpaperHost.csproj" -c Release
dotnet build "samples\WebWallpaperParent\WebWallpaperParent.csproj" -c Release
```

本示例为了离线可编译，直接引用了 `lib/` 下的：

- `Microsoft.Web.WebView2.Core.dll`：SDK 1.0.4314.35（net462 程序集，实测可在 .NET 8 进程加载）
- `WebView2Loader.dll`：x64 native loader
- `WebView2-LICENSE.txt`

WebView2 Runtime 需要系统安装；本机为 **156.0.4314.50**，启动时通过 `GetAvailableBrowserVersionString()` 校验。

> 如果在低完整性工作区构建，EXE 可能继承 Low 标签，`SetParent` 会失败。  
> 请从桌面原生 PowerShell 构建，或构建到 `%USERPROFILE%\WallerpaperBuild`：

```powershell
$build = "$env:USERPROFILE\WallerpaperBuild"
dotnet build "samples\WebWallpaperHost\WebWallpaperHost.csproj" -c Release -o "$build\webhost"
dotnet build "samples\WebWallpaperParent\WebWallpaperParent.csproj" -c Release -o "$build\webparent"
```

## 运行

```powershell
$build = "$env:USERPROFILE\WallerpaperBuild"
& "$build\webparent\WebWallpaperParent.exe" `
  --child "$build\webhost\WebWallpaperHost.exe" `
  --html  "$build\webhost\wwwroot\index.html" `
  --seconds 15
```

在线 URL：

```powershell
& "$build\webparent\WebWallpaperParent.exe" `
  --child "$build\webhost\WebWallpaperHost.exe" `
  --url "https://example.com/wallpaper.html"
```

参数：

| 参数 | 说明 |
|---|---|
| `--html <index.html>` | 本地 HTML；只接受 `.html/.htm` |
| `--url <https://...>` | 在线页面；只接受 https |
| `--seconds N` | 自动测试/自动停止；0 为交互模式 |
| `--idle-seconds N` | 无鼠标/音频活动后进入 idle，默认 15s |
| `--no-audio` | 不启动 WASAPI loopback |
| `--no-gpu` | 给 WebView2 加 `--disable-gpu`，软件渲染 |

交互命令（父进程）：

```text
p=pause  r=resume  s=stats  q=stop
```

## WebView2 初始化关键配置

代码：[`WebViewHost.cs`](WebViewHost.cs)

| 配置 | 值 | 原因 |
|---|---|---|
| `CoreWebView2Environment.CreateAsync(null, udf, options)` | `udf = %TEMP%\Wallerpaper\WebView2\<id>` | 每个壁纸独立 profile，避免共享浏览器进程/互相影响；退出时删除 |
| `CreateCoreWebView2ControllerAsync(hwnd)` | WorkerW 子窗口 HWND | raw controller，不用 WinForms WebView2 控件，不反射私有 controller |
| `Bounds` | `0,0,clientW,clientH` | 全屏铺满 WorkerW 客户区 |
| `RasterizationScale` | `GetDpiForWindow/96` | 与 PerMonitorV2 物理像素对齐 |
| `ShouldDetectMonitorScaleChanges` | `false` | DPI 由宿主显式控制，避免混合 DPI 抖动 |
| `DefaultBackgroundColor` | 黑色 | 防止页面未绘制时白闪 |
| `IsVisible` | `true` | 壁纸可见；pause 时才隐藏并 suspend |
| `--disable-background-networking` | 是 | 禁用后台网络/组件流量 |
| `--disable-component-update` | 是 | 不让浏览器在壁纸进程里更新组件 |
| `--disable-sync` | 是 | 禁用账号同步 |
| `--disable-breakpad` / `--disable-crash-reporter` | 是 | 不在壁纸进程堆崩溃上报 |
| `--disable-extensions` | 是 | 不让扩展扩大攻击面 |
| `--disable-default-apps` | 是 | 减少默认应用/后台页 |
| `--no-first-run` / `--no-default-browser-check` | 是 | 固定首次启动行为 |
| `--autoplay-policy=no-user-gesture-required` | 是 | 允许页面自动播放音视频（内容仍需可控） |
| `--disable-gpu` | 仅 `--no-gpu` | 硬件加速可配置；默认开启 |
| `AreDefaultContextMenusEnabled` | false | 禁用右键菜单 |
| `AreDevToolsEnabled` | false | 禁用 DevTools |
| `AreHostObjectsAllowed` | false | 不注入 host object |
| `IsWebMessageEnabled` | true | 仅启用受控 JS bridge |
| `AreBrowserAcceleratorKeysEnabled` | false | 禁用浏览器快捷键 |
| `IsZoomControlEnabled` | false | 禁止 Ctrl+滚轮缩放 |
| `IsSwipeNavigationEnabled` / `IsPinchZoomEnabled` | false | 禁止手势导航/缩放 |
| `AreDefaultScriptDialogsEnabled` | false | 禁止 alert/confirm |
| `IsStatusBarEnabled` | false | 不显示状态栏 |
| `IsGeneralAutofillEnabled` / `IsPasswordAutosaveEnabled` | false | 禁用自动填充/密码保存 |

安全事件：

- `NewWindowRequested` → `Handled=true`，禁止弹窗/新窗口。
- `NavigationStarting` → 只允许 `https://wallerpaper.local/`（本地）或指定 https host；其他取消。
- `PermissionRequested` → 全部 Deny（摄像头/麦克风/定位/通知等）。
- `DownloadStarting` → Cancel。
- `ProcessFailed` → 上报错误，父进程停止/重启。
- 页面 → Host 的 `WebMessageReceived` 只写日志，不执行任何宿主命令。

## 本地文件访问范围

- 只接受 `.html/.htm` 文件。
- 用 `SetVirtualHostNameToFolderMapping("wallerpaper.local", htmlDirectory, DenyCors)` 把 **HTML 所在目录** 映射为虚拟主机。
- 导航只允许 `https://wallerpaper.local/` 前缀；不使用 `file://`。
- 页面只能访问该目录内的资源；不能通过 `file://` 或任意本地路径读取整个磁盘。
- 在线模式只允许 https，顶层导航限定到初始 URL host；不开启 `--disable-web-security`。
- 右键、拖拽、文本选择、滚动条在 JS shim 中再次禁用（CSS + `preventDefault`）。

## 鼠标注入

WorkerW 宿主和 WebView2 子窗口都设置：

```text
WS_EX_TRANSPARENT | WS_EX_NOACTIVATE
```

因此桌面点击仍然穿透到图标/其他窗口。宿主每 16ms（idle 时 100ms）轮询：

```csharp
GetCursorPos(out screen);
ScreenToClient(hostHwnd, ref screen);
x = screen.X / controller.RasterizationScale;
y = screen.Y / controller.RasterizationScale;
_core.PostWebMessageAsJson({ type: "wallerpaper.mouse", x, y, inside });
```

JS bridge：

```js
window.wallerpaper.mouse = { x, y, inside };
window.wallerpaper.onMouse = fn;
window.addEventListener('wallerpaper:mousemove', e => { /* e.detail */ });
// 同时向 document 派发合成 MouseEvent，兼容普通 mousemove 监听
```

注意：合成 `MouseEvent` 的 `isTrusted=false`，不能用于需要真实用户手势的 API（全屏、某些媒体权限）。壁纸默认点击穿透；需要点击交互时应做“热键临时接管”模式，而不是依赖穿透窗口。

## 音频频谱注入

父进程 `AudioSpectrum`：

- NAudio `WasapiLoopbackCapture` 捕获默认扬声器（系统混音）的 loopback。
- 1024 点 FFT + Hann 窗，32 个对数频段（40Hz–16kHz），归一化到 0..1。
- 每 33ms 通过 stdio 发送：

```json
{"cmd":"audio","bins":[0.1,...],"level":0.42}
```

子进程：

```csharp
_core.PostWebMessageAsJson({ type = "wallerpaper.audio", bins, level });
```

JS：

```js
window.wallerpaper.audio = { bins: number[], level: number };
window.wallerpaper.onAudio = fn;
window.addEventListener('wallerpaper:audio', e => { /* e.detail.bins */ });
```

实测：播放 440Hz 测试音时 `audioLevel=1`、`audioBins=32`，页面 bridge 收到消息。

限制：只抓默认输出设备；不支持按进程抓取、麦克风、独占模式；WASAPI loopback 在静音/无音频端点时可能没有数据。

## 闲置降频

- 鼠标/音频活动会刷新 `_lastActivityUtc`。
- 超过 `--idle-seconds`：
  - 向页面发送 `{type:"wallerpaper.idle", idle:true}`；
  - JS 将 `requestAnimationFrame` 包装为约 15fps；
  - 鼠标轮询从 16ms 降到 100ms。
- 鼠标移动或音频 level > 0.02 时恢复。
- `pause` 命令使用 `TrySuspendAsync()` + `IsVisible=false`；`resume` 用 `IsVisible=true` 恢复。
- 没有使用 `DebugActiveProcess` 挂起 WebView2 子进程（Lively 的做法），避免调试权限/EDR 风险。

## 内存泄漏防范

1. **独立 UserDataFolder**：每个实例 `%TEMP%\Wallerpaper\WebView2\<id>`，避免共享 profile 的进程/缓存耦合。父进程显式传入 `--user-data-folder`，子进程不删除用户指定目录；父进程关闭 Job 后再删除（20×200ms 重试）。单独运行子进程时由子进程尝试删除（8×150ms）。
2. **官方关闭路径**：`CoreWebView2.Stop()` → `controller.Close()` → `Dispose()`；不用 `Process.Kill` 作为首选。
3. **父进程 Job Object**：`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`，父进程崩溃/退出时连带杀掉 Host 和 WebView2 浏览器/GPU/渲染进程，避免 `msedgewebview2.exe` 残留。
4. **子进程退出兜底**：父进程 8s 未退出则 `Kill(entireProcessTree:true)`。
5. **计时器释放**：mouse/idle/style timer 在 Dispose 中停止/Dispose。
6. **不注册长期 host object**：`AreHostObjectsAllowed=false`，避免 JS 持有 .NET 对象引用。
7. **不共享 WebView2 控件**：raw controller 只绑定当前 WorkerW；窗口丢失直接子进程退出，由父进程重启，不做原地重挂。
8. **进程失败处理**：`ProcessFailed` 上报并退出，不留下半死浏览器进程。
9. **不启用后台同步/组件更新/扩展**：减少后台页面和网络缓存。

实测停止后：无本实例 `msedgewebview2.exe` 残留，`%TEMP%\Wallerpaper\WebView2` 下无实例目录。

## 与 Wallpaper Engine Web 壁纸的差异

| 维度 | 本实现 | Wallpaper Engine |
|---|---|---|
| 浏览器内核 | **WebView2 / Edge Chromium**，随系统 Evergreen 更新 | CEF/Chromium，随 WE 打包 |
| 内容 | 本地 HTML 目录、https URL | 本地 web 壁纸包、Workshop、编辑器工程 |
| 属性系统 | 无 WE 的 `project.json` 属性/可配置项 | 完整属性、主题、用户设置 |
| 鼠标 | 默认穿透；**合成鼠标事件/自定义事件**，`isTrusted=false` | 支持交互模式，真实输入路径更完整 |
| 音频可视化 | 父进程 WASAPI loopback + FFT → JS 频谱 | 内置音频可视化/音频处理链，支持更多音源 |
| 多显示器 | 当前示例单主屏；架构上一个实例一个 Host | 完整多屏/复制/跨屏 |
| 性能档位 | idle rAF 降频、pause suspend、独立 profile、GPU 可关 | 质量/帧率/暂停/性能预设更完整 |
| 安全 | 严格 allowlist、禁弹窗/下载/权限/host object/DevTools | 功能优先，面更大；有自身安全设置 |
| 分发 | GPL-3.0 + 自有安装器 | Steam/Workshop 生态 |
| HDR | 不承诺；按 SDR 处理 | 有自身 HDR/色彩处理策略 |
| 进程模型 | 父 + Web Host + WebView2 浏览器进程树 + Job 兜底 | 类似多进程，但由 WE 自身管理 |
| 开发成本 | 需要自己实现桥、属性、资源打包、预览 | 成熟工具链/商店/文档 |

## 交互测试页

测试页：[`wwwroot/test-interaction.html`](wwwroot/test-interaction.html)

它提供：

- 跟随 `window.wallerpaper.mouse` 的十字准星、光环和粒子；
- 32 段音频柱；
- HUD 显示 `pageClicks` / `nativeClicks`；
- 页面内放了一个“CLICK TRAP”，任何到达页面的原生点击都会计数并 post 到宿主；
- `test-mouse` / `test-audio` / `test-click-reached` 通过 WebMessage 回传宿主日志。

运行：

```powershell
$build = "$env:USERPROFILE\WallerpaperBuild"
& "$build\webparent\WebWallpaperParent.exe" `
  --child "$build\webhost\WebWallpaperHost.exe" `
  --html  "$build\webhost\wwwroot\test-interaction.html" `
  --seconds 15 --test-mouse-sweep
```

`--test-mouse-sweep` 是测试钩子：在页面加载后发送 300,300 → 600,400 → 900,500 → 1200,600，验证页面坐标桥，不依赖物理鼠标。

实测结果：

- 页面 `test-mouse` 收到 300/300、600/400、900/500、1200/600（与真实鼠标坐标交替）；
- 页面 `test-audio` 收到 32 bins，level 在约 0.39–1.0 之间变化；
- 真实点击后 `pageClicks=0`、`nativeClicks=0`，没有触发页面元素；
- 桌面暴露点 `WindowFromPoint` 返回桌面/空窗口，不是 WebView2；
- 停止后无 Host、浏览器进程、UserDataFolder 残留。

限制：当前测试终端的窗口覆盖了大部分桌面，所以没有自动点到具体桌面图标；页面点击计数为 0 证明点击没有被 WebView2 消费，但“图标被选中/打开”的最终视觉确认建议在无遮挡桌面手动点一次图标。

## 已验证

- 本地 `wwwroot/index.html` 加载成功（`https://wallerpaper.local/index.html`）。
- WebView2 Runtime 156.0.4314.50；GPU 开启和 `--no-gpu` 两种模式均初始化成功。
- JS bridge：`installed=true`、`deliver=function`、`msgCount` 持续增长（实测 66→118）。
- 鼠标：页面状态收到实际坐标与 `inside=true`。
- 音频：440Hz 测试音，`audioLevel=1`、`audioBins=32` 到达页面。
- idle：5s 无活动后 `idle: JS rAF throttled`。
- stop：`STOPPED exit=0`；无本实例浏览器进程/UserDataFolder 残留，父进程 Job 关闭后目录删除成功。

## 已知限制

1. 页面若检查 `event.isTrusted`，合成鼠标事件无效；需要真实输入时用热键接管/组合控制器。
2. 在线页面的子资源没有逐条 host allowlist；顶层导航严格，子资源由页面自身 CSP 约束。生产环境建议再加 `WebResourceRequested`/CSP。
3. 只抓默认输出设备的系统音频；静音/无端点时频谱可能为 0。
4. idle 只节流 rAF；页面用 `setInterval`/Worker/WebGL 自己循环时仍需页面配合。
5. 未做 WebView2 Fixed Version 分发；Runtime 更新可能改变行为。
6. 当前单主屏；多屏/混合 DPI 的 Bounds/RasterizationScale 需要接主程序显示器编排。
7. SDK 的 .NET 程序集是 net462 资产（实测 net8 可加载）；如未来 SDK 改为仅 netcoreapp，需要更新引用方式。
