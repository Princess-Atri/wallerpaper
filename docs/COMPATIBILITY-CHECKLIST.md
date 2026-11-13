# Wallerpaper 兼容性检查清单

## 1. 操作系统

| 系统 | 支持 | 说明 |
|---|---|---|
| Windows 10 1809 (17763) | ✅ 最低 | PerMonitorV2、DWM、WebView2、WASDK 1.7 |
| Windows 10 1903–22H2 | ✅ | 推荐 21H2+ |
| Windows 11 21H2–23H2 | ✅ | 含 raised desktop 路径 |
| Windows 11 24H2/25H2 | ✅ | 重点回归 WorkerW |
| Windows Server | ⚠️ 非目标 | 无 Explorer 桌面不保证 |
| Windows 7/8 | ❌ | 不支持 WinUI 3/WebView2 现代组合 |

安装前置：

- WebView2 Runtime（Web 壁纸必需）
- VC++ 2015-2022 Redistributable（libmpv/WebView2 原生依赖）
- .NET 不需要用户安装（安装器 self-contained）

## 2. API 兼容性

| API | 最低系统 | 用途 | 兜底 |
|---|---|---|---|
| `SetProcessDpiAwarenessContext` | Win10 1703 | PerMonitorV2 | `SetProcessDPIAware` |
| `DwmGetWindowAttribute` | Vista/Win7 | frame bounds/cloaked | `GetWindowRect` |
| `SHQueryUserNotificationState` | Vista | 全屏提示 | 仅作 hint |
| `IDesktopWallpaper` | Win8 | 静态壁纸降级 | 停止渲染 |
| WorkerW / `0x052C` | Win7+（未文档化） | 桌面挂靠 | 已有 WorkerW/Progman |
| raised desktop | Win11 | 新桌面层级 | 经典 WorkerW |
| WebView2 | Win10+ | Web 壁纸 | 禁用 Web 分类 |

WorkerW 不是公开 API：Explorer 更新可能改变层级，必须有“挂靠失败 → 不崩溃 → 日志”兜底。

## 3. 高 DPI / 多屏

目标：**PerMonitorV2 + 物理像素**。

| 检查项 | 期望 |
|---|---|
| 主程序 manifest | `PerMonitorV2` |
| 调度器枚举 | `EnumDisplayMonitors` 物理 rect |
| 子进程窗口 | `--bounds` 使用物理像素 |
| Scene | D3D11 client 物理像素，swapchain 不缩放 |
| Video | WorkerW client 物理像素，mpv 填满 |
| Web | `GetDpiForWindow` → `RasterizationScale` |
| 跨屏拖动 | 不拉伸、不模糊、不黑边 |
| 100% | 正常 |
| 125% | 正常 |
| 150% | 正常 |
| 175%/200% | 正常 |
| 主屏 125% + 副屏 100% | 各自独立 |
| 主屏 4K + 副屏 1080p | 各自 swapchain/窗口 |

已知风险：

- 混合 DPI 下若子进程没有 PerMonitorV2，Windows 会虚拟化坐标，壁纸偏移。
- WebView2 需要 `RasterizationScale`，否则页面按 100% 渲染后放大。
- 当前环境只有单屏 125%，混合 DPI 仍需双屏实机验收。

## 4. 双显卡笔记本

目标：**不强制、可预测、可回退**。

策略：

1. 默认让 Windows 图形设置决定 GPU：
   - 设置 → 系统 → 显示 → 图形 → 添加 `WallerpaperShell.exe` / `VideoWallpaperHost.exe`
   - 选择“高性能”
2. mpv 优先 `d3d11va`，失败回退 `dxva2`，再回退软件（显式 `--allow-software`）。
3. WebView2 跟随 Chromium GPU 选择；如黑屏，禁用 GPU 或升级驱动。
4. Scene 使用 D3D11 默认适配器；如输出在 iGPU/解码在 dGPU，会在日志中记录 adapter，后续可加 `EnumAdapterByGpuPreference(HighPerformance)`。
5. 电池模式不主动强制 dGPU；由 Windows 电源/图形策略决定。
6. 外接显示器直连 dGPU 时，优先把壁纸放到该输出。

验收：

- [ ] iGPU 输出 + dGPU 解码无黑屏/拷贝卡顿
- [ ] dGPU 输出正常
- [ ] 混合模式切换不崩溃
- [ ] 电池/AC 切换不重启渲染器（仅 FPS）

## 5. 全屏/多屏行为

- [ ] 全屏游戏暂停对应显示器
- [ ] 独占全屏（`QUNS_RUNNING_D3D_FULL_SCREEN`）暂停
- [ ] borderless 全屏窗口暂停
- [ ] 最大化窗口默认不触发，`triggerOnMaximized` 可开
- [ ] 全屏视频切到后台仍暂停（媒体白名单）
- [ ] 任务管理器/设置不误触发
- [ ] 多屏只暂停全屏所在屏
- [ ] 热插拔不崩溃、不残留子进程

## 6. 安装/卸载/自启动

- [ ] HKCU Run，无 UAC
- [ ] 安装目录 `%LOCALAPPDATA%\Programs\Wallerpaper`
- [ ] 桌面/开始菜单快捷方式
- [ ] 卸载删除目录、Run、Uninstall、快捷方式、LocalAppData 数据
- [ ] 重启登录后最小化启动
- [ ] 移动安装目录后重新自启动
- [ ] 杀进程后无残留 Host/渲染子进程（Shell Job Object）

## 7. 日志与崩溃

- [ ] Shell/Host/Scene/Video/Web 各自日志
- [ ] 日志轮转 10 MB、保留 7 天
- [ ] 目录不可写回退 `%TEMP%`
- [ ] 托管异常生成 dump
- [ ] 原生崩溃过滤生成 dump
- [ ] dump 可用 WinDbg/VS 打开
- [ ] 卸载时清理日志/dump

## 8. 资源验收

建议阈值（1080p/1200p，单屏）：

| 场景 | 工作集 | VRAM | CPU |
|---|---:|---:|---:|
| Shell 闲置 | < 300 MB | 少量 | < 1% |
| Scene 基础 | < 150 MB | < 50 MB | < 2% |
| Video 播放 | < 250 MB | < 300 MB | < 5% |
| Video 暂停 | < 150 MB | 降低 | < 1% |
| Web 基础页 | < 400 MB 总计 | < 300 MB | < 5% |

实机测量见 [`RESOURCE-METRICS.md`](RESOURCE-METRICS.md)。

## 9. 回归命令

```powershell
dotnet build Wallerpaper.sln -c Release
powershell -ExecutionPolicy Bypass -File tools\ui-flow-test.ps1
powershell -ExecutionPolicy Bypass -File tools\build-release.ps1
```

安装/卸载测试建议在**干净标准用户**和**非 DSH 沙箱**中执行；Low integrity 会阻止 HKCU Uninstall 和 LocalAppData Programs 写入。
