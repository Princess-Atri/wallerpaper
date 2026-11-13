# Wallerpaper

> 对标 Wallpaper Engine / Lively 的 Windows 动态壁纸引擎  
> C# + WinUI 3 + .NET 10 + Win32 P/Invoke  
> 状态：**设计基线 v0.1 / 决策已确认**，Phase 0 待开工。

## 先读

- 完整设计（架构 / 路线 / 风险 / 选型）：[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)
- Lively 源码对照：见设计文档第 5 节（固定 commit，便于复核）
- API 验证表：见设计文档第 6 节
- ADR 索引：[`docs/adr/README.md`](docs/adr/README.md)
- Phase 0 最小 Demo：[`samples/WorkerW/`](samples/WorkerW/)（WorkerW 挂靠 + Explorer 重启 + DPI + 清理）
- 阶段 2 视频子进程：[`samples/VideoWallpaperHost/`](samples/VideoWallpaperHost/)（官方 libmpv-2.dll 已放入 sample 根目录；D3D11VA + stdio IPC + 暂停释放 + FPS 滤镜）
- 阶段 2 测试报告：[`docs/TEST-REPORT.md`](docs/TEST-REPORT.md)（10 分钟稳定性、损坏视频、CPU/内存数据）
- 阶段 3 Web 壁纸：[`samples/WebWallpaperHost/`](samples/WebWallpaperHost/)（WebView2 + 共享 WorkerW + 鼠标/音频 JS bridge + 安全 allowlist）
- 共享 WorkerW 基类：[`samples/Shared/WorkerWRuntime/`](samples/Shared/WorkerWRuntime/)（视频/Web/Scene 子进程共用）
- 阶段 4 场景渲染：[`samples/SceneWallpaperHost/`](samples/SceneWallpaperHost/)（Silk.NET D3D11 + 图层 + GLSL + QuickJS + 音频 FFT）
- 阶段 4 场景说明：[`samples/SceneWallpaperHost/README.md`](samples/SceneWallpaperHost/README.md)
- 阶段 5 主调度器：[`samples/WallerpaperHost/`](samples/WallerpaperHost/)（全屏检测 + 三档策略 + 多显示器 + 看门狗 + 电源）
- 阶段 5 状态机/调度说明：[`samples/WallerpaperHost/README.md`](samples/WallerpaperHost/README.md)
- 阶段 6 WinUI 3 主界面：[`samples/WallerpaperShell/`](samples/WallerpaperShell/)（分类/缩略图/属性/设置/托盘/自启动/JSON 配置）
- 阶段 6 界面说明：[`samples/WallerpaperShell/README.md`](samples/WallerpaperShell/README.md)
- 阶段 6 用户流程测试报告：[`docs/PHASE6-TEST-REPORT.md`](docs/PHASE6-TEST-REPORT.md)
- Visual Studio 解决方案：[`Wallerpaper.sln`](Wallerpaper.sln)
- 阶段 7 产品化：[`docs/PRODUCTIZATION.md`](docs/PRODUCTIZATION.md)
- 兼容性检查清单：[`docs/COMPATIBILITY-CHECKLIST.md`](docs/COMPATIBILITY-CHECKLIST.md)
- 资源占用指标：[`docs/RESOURCE-METRICS.md`](docs/RESOURCE-METRICS.md)
- 单文件安装器：[`samples/Installer/WallerpaperSetup/`](samples/Installer/WallerpaperSetup/)
- 安装包产物：[`artifacts/WallerpaperSetup.exe`](artifacts/WallerpaperSetup.exe)

## 已确认决策

| 决策 | 结论 |
|---|---|
| 许可证 | **GPL-3.0**；可复用 Lively 代码，保留版权/来源；libmpv/FFmpeg 做 GPL 兼容与源码分发 |
| 运行时 | **.NET 10 LTS**（.NET 8 于 2026-11 EOL） |
| UI | **WinUI 3**（Fluent/Mica；Windows App SDK 自包含或安装器部署） |
| 场景渲染 | **Vortice.Windows** D3D11；Silk.NET 仅作备选 |
| 1.0 范围 | **视频 + Web + 场景**（场景在 Phase 3） |
| 进程模型 | 主 UI/编排进程 + 每个“壁纸实例 × 显示器”一个 RenderHost |
| IPC | stdin/stdout 匿名管道 + 长度前缀 + MessagePack |
| 视频 | libmpv `wid` 进程内嵌入；`IVideoEngine` 保留 `mpv.exe` 兜底 |
| Web | WebView2（raw controller + WinForms 消息循环宿主） |
| 桌面挂靠 | 经典 WorkerW + Win11 raised desktop 双路径 |
| 全屏/显存 | 分级释放 + 静态快照兜底 + Job Object |
| 安装/更新 | WinUI 3 非打包 + .NET 10/Windows App SDK 自包含 + Velopack；MSIX 只用于商店 |

## 部署前置

- **Windows App Runtime**：WinUI 3 非打包需要；优先自包含 Windows App SDK，或由安装器部署 Runtime。
- **WebView2 Runtime**：Web 壁纸必需；安装器检测/引导。
- **VC++ Redistributable**：仅在 libmpv/原生依赖需要时安装。
- **.NET 10**：自包含发布，不要求用户预装。

## 下一步（Phase 0）

1. `spike-workerw`：经典 WorkerW / raised desktop / 图标交互 / Explorer 重启。
2. `spike-scene`：D3D11 layered + HLSL + 设备丢失（Vortice）。
3. `spike-web`：raw WebView2 + DPI/文本缩放 + TrySuspendAsync。
4. `spike-video`：libmpv `wid` + 暂停/停止/杀进程的 VRAM 曲线。
5. `spike-ipc`：Job Object + stdio MessagePack + 杀进程矩阵。

Phase 0 结束出测量报告，再冻结 1.0 详细排期。

## 计划中的仓库结构

```text
src/
  Wallerpaper.App/            # WinUI 3
  Wallerpaper.Core/           # 编排，无 UI 依赖
  Wallerpaper.Contracts/      # IPC DTO
  Wallerpaper.Interop/        # Win32 P/Invoke
  Wallerpaper.RenderHost/     # 每壁纸实例 × 显示器
  Wallerpaper.Render.Video/   # libmpv
  Wallerpaper.Render.Web/     # WebView2
  Wallerpaper.Render.Scene/   # D3D11 / Vortice
  Wallerpaper.Tools/          # 打包/校验/缩略图
tests/
docs/
```

## 参考

- Lively（GPL-3.0，可复用）：<https://github.com/lively-community/lively>
- 调研基线 commit：`25a0a5f4ed1988ff42117d3a17feb321062237fe`
- .NET 支持策略：<https://learn.microsoft.com/en-us/dotnet/core/releases-and-support>
- Windows App SDK 非打包部署：<https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/deploy-unpackaged-apps>
- WinUI 3：<https://learn.microsoft.com/en-us/windows/apps/winui/winui3/>
- WebView2：<https://learn.microsoft.com/en-us/microsoft-edge/webview2/>
- Vortice.Windows：<https://github.com/amerkoleci/Vortice.Windows>
- H.NotifyIcon：<https://github.com/HavenDV/H.NotifyIcon>

## 许可证

本项目采用 **GPL-3.0**。复用 Lively 代码时必须保留版权头、LICENSE 和来源 commit；libmpv/FFmpeg 的源码与许可以及第三方 NOTICE 需随分发提供。
