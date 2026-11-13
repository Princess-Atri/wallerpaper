# WorkerW 桌面挂靠 Demo（.NET 8）

单文件源码：[`Program.cs`](Program.cs)  
构建壳：[`WorkerW.csproj`](WorkerW.csproj)（`net8.0-windows`，x64）

## 运行

```powershell
cd "D:\Desktop\wallerpaper c\samples\WorkerW"
dotnet run -- --seconds 8
# 只发现 Progman/WorkerW/DefView，不创建窗口：
dotnet run -- --dry-run
```

要求：

- Windows 10 1809+ / Windows 11，x64。
- .NET 8 SDK（或 .NET 8 运行时 + 已构建的 exe）。
- **普通用户完整性**：不要用管理员权限，也不要在低完整性沙箱里运行。`SetParent` 到 Explorer 在低完整性下会返回 `ACCESS_DENIED (5)`。
- 关闭/最小化全屏浏览器等覆盖桌面的窗口，否则看不到纯色窗口，`WindowFromPoint` 也会命中它们。

## 预期行为

- 主显示器被纯色（RGB 24,32,48）铺满。
- 窗口无边框、无标题、无任务栏按钮、不出现在 Alt+Tab。
- 鼠标点击穿透；桌面图标、右键菜单、框选仍由 Explorer 处理。
- Ctrl+C 或 `--seconds` 到期后：先 `SetParent(NULL)`、恢复 `WS_POPUP`、再 `DestroyWindow`；桌面恢复。
- Explorer 重启：`TaskbarCreated` + 1s 看门狗检测到 WorkerW/窗口失效，自动重建并重新挂靠。

## 验证步骤

1. 运行 `--seconds 15`，确认桌面变纯色、图标仍可单击/双击/右键。
2. 在**普通权限**的另一个 PowerShell 中测试 Explorer 重启（会关闭文件资源管理器窗口，建议在 VM 中做）：
   ```powershell
   Stop-Process -Name explorer -Force
   # 若系统没有自动重启 Explorer：
   Start-Process explorer.exe
   ```
   观察日志出现 `[TaskbarCreated]` 和 `re-attach succeeded`。
3. DPI：设置 → 显示 → 缩放 100/125/150/200%，或改分辨率；窗口应在 1s 内重新匹配 `rcMonitor`。
4. 退出后确认：任务管理器无残留进程、桌面图标正常、无异常窗口。

## 已知限制

- `0x052C` 是 **未文档化** 的 Progman 消息；不同 Windows build 可能无响应或行为不同。
- Windows 11 的 raised desktop（`Progman` 带 `WS_EX_NOREDIRECTIONBITMAP`）走 Progman + `SHELLDLL_DefView` z-order，而不是经典 WorkerW。
- 只覆盖主显示器；多屏需要为每个 `HMONITOR` 建窗口。
- `WS_EX_LAYERED` + D3D11 flip model 的兼容性不在本 demo 范围（纯 GDI 填充）。
- 第三方 Explorer 修改器（ExplorerPatcher/StartAllBack 等）可能改变窗口树，需要单独测试。
- 本 demo 不修改静态壁纸，不做显存释放，不做 Web/视频渲染。

## WorkerW 失效时的降级：DWM 缩略图

如果 `SetParent`/WorkerW 路径不可用，可把“渲染”和“呈现”解耦：

1. 创建一个隐藏的顶层 **source** 窗口，渲染器（D3D11/WebView2/视频）只渲染到 source。
2. 创建一个 **presenter** 窗口放在桌面 z-order 底部，调用：
   - `DwmRegisterThumbnail(presenter, source, out thumbnail)`
   - `DwmUpdateThumbnailProperties(thumbnail, DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE | DWM_TNP_SOURCECLIENTAREAONLY, ...)`
3. presenter 负责点击穿透/`WS_EX_NOACTIVATE`/`WS_EX_TOOLWINDOW`；source 不需要跨进程 `SetParent`。

注意：DWM 缩略图只是“把 source 镜像到 presenter”，**不是桌面壁纸 API**，也不改变 z-order 本质。若 presenter 在图标之上，仍可能盖住图标；若在图标之下，可能被 DefView 遮住。它适合“渲染窗口无法被 SetParent”或“需要跨进程解耦”的场景，不能替代 WorkerW 的层级 hack。真正的兜底仍是静态壁纸/截图。

## Win32 依据

代码关键步骤均注释了 MSDN 链接；`0x052C` 与 Lively 的对应实现在 `Program.cs` 顶部注释中列出（固定 commit）：

<https://github.com/lively-community/lively/blob/25a0a5f4ed1988ff42117d3a17feb321062237fe/src/Lively/Lively/Core/WinDesktopCore.cs>
