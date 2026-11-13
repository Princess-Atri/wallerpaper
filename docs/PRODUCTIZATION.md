# Wallerpaper 产品化收尾

## 1. 发布产物

| 产物 | 路径 | 说明 |
|---|---|---|
| 单文件安装器 | `artifacts/WallerpaperSetup.exe` | 一个 EXE，内含完整 payload（约 190 MB） |
| 自包含发布目录 | `artifacts/publish/` | 便携/调试用，包含 WinUI 运行时和三类 Host |
| payload | `samples/Installer/WallerpaperSetup/Resources/payload.zip` | 安装器嵌入的压缩包（约 155 MB） |
| 符号 | `WallerpaperShell.pdb` 等 | 保留用于 dump 分析 |

安装器为 **per-user**：

```text
%LOCALAPPDATA%\Programs\Wallerpaper
```

不写 Program Files、不需要管理员权限，因此不会触发 UAC。

### 单文件 App 的真实结论

测试过：

```text
dotnet publish WallerpaperShell -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

结果生成了约 168 MB 的单文件 EXE，但运行失败：

```text
ClassFactory cannot supply the requested class
  at Microsoft.UI.Xaml.Application.Start(...)
```

原因是 WinUI 3 / Windows App SDK 依赖非托管 Windows App Runtime、
`resources.pri`、XAML 元数据和多个原生 DLL，当前版本不支持把 WinUI App
压成一个可运行 EXE。

**产品化采用“单文件安装器 + 应用目录”方案**，并且 Shell 与
WallerpaperHost、Scene/Video/Web 四个 Host 全部按 **self-contained** 发布，
避免目标机器缺少 .NET 8 时 Host 提示 “You must install or update .NET”。

- 用户只拿到一个 `WallerpaperSetup.exe`
- 安装器内嵌全部依赖
- 安装时解包到 LocalAppData
- 卸载时删除完整目录

这是 Windows App SDK 应用现实可用的“单文件交付”形式。

## 2. 安装与卸载

代码：[`samples/Installer/WallerpaperSetup/Program.cs`](../samples/Installer/WallerpaperSetup/Program.cs)

### 安装

1. 停止旧 Shell/Host/渲染子进程
2. 解包 payload 到安装目录（staging 在安装目录旁，不使用 `%TEMP%`）
3. 复制 `WallerpaperSetup.exe` 自身
4. 创建桌面/开始菜单快捷方式
5. 写 HKCU Run：
   ```text
   WallerpaperShell = "<install>\WallerpaperShell.exe" --minimized
   ```
6. 写 HKCU Uninstall：
   ```text
   HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Wallerpaper
   ```
7. 启动 Shell

全部是 HKCU/用户目录，**不触发 UAC**。

### 卸载

`WallerpaperSetup.exe --uninstall`：

- 停止 Shell/Host/Video/Scene/Web 进程
- 删除 HKCU Run
- 删除 Uninstall 注册表项
- 删除桌面/开始菜单快捷方式
- 删除安装目录
- 删除 `%LOCALAPPDATA%\Wallerpaper`（配置、缓存、日志、dump）
- 如果卸载器位于安装目录内，先复制到临时目录再自删
- 进程占用导致删不掉时，延迟 `rmdir` 兜底

用户壁纸素材目录（Videos/Documents）不会被删除。

## 3. 自启动

使用 HKCU Run，不用计划任务、不用服务、不触发 UAC：

```text
HKCU\Software\Microsoft\Windows\CurrentVersion\Run
WallerpaperShell = "<install>\WallerpaperShell.exe" --minimized
```

`--minimized`：

- 主窗口不显示
- 托盘图标存在
- 调度器与当前显示器壁纸继续启动

## 4. 日志与 Dump

共享库：

[`samples/Shared/Wallerpaper.Diagnostics/`](../samples/Shared/Wallerpaper.Diagnostics/)

### 日志

```text
%LOCALAPPDATA%\Wallerpaper\logs\<process>-yyyyMMdd.log
```

- `WallerpaperShell-*.log`
- `WallerpaperHost-*.log`
- `SceneWallpaperHost-*.log`
- `VideoWallpaperHost-*.log`
- `WebWallpaperHost-*.log`

规则：

- UTF-8
- 每行：`时间 [级别] [PID] 消息`
- 单文件 10 MB 轮转 `.1`
- 保留 7 天
- 目录不可写时回退到 `%TEMP%\Wallerpaper\logs`
- 日志失败绝不抛出到主流程

### Dump

```text
%LOCALAPPDATA%\Wallerpaper\dumps\<process>-yyyyMMdd-HHmmss-<pid>.dmp
```

- `MiniDumpWriteDump`
- `MiniDumpWithFullMemory`、Handle/UnloadedModules/ThreadInfo/Token
- 托管未处理异常 → dump
- `SetUnhandledExceptionFilter` 原生崩溃 → dump
- 实测 dump 约 112 MB（.NET + WinUI 进程）
- Shell、Host、Scene/Video/Web 分别安装 handler

验证：

```text
[INFO] diagnostics smoke start
[ERROR] dump written: ...dmp (smoke)
```

## 5. 兼容性摘要

详见 [`COMPATIBILITY-CHECKLIST.md`](COMPATIBILITY-CHECKLIST.md)。

- Windows 10 1809 (17763) 起步
- Windows 11 21H2–25H2
- 需要 WebView2 Runtime（Web 壁纸）
- WorkerW 是未文档化机制，必须保留失败兜底
- 高 DPI：PerMonitorV2 + 物理像素 + 每屏 DPI
- 双显卡：由 Windows 图形设置决定高性能 GPU，不强制在电池下切 dGPU

## 6. 资源指标

详见 [`RESOURCE-METRICS.md`](RESOURCE-METRICS.md)。

| 场景 | 工作集 | 私有内存 | VRAM | CPU/GPU |
|---|---:|---:|---:|---:|
| Shell 闲置 | ~162.6 MB | ~98.2 MB | 少量 | <1% |
| Scene audio_gradient 1920×1200 | 84–95 MB | 78–87 MB | 16.7 MB | CPU 0.4–0.8%，GPU 0.1ms |
| Video 1080p H.264 d3d11va | 156 MB 播放 | 248 MB | 未暴露 | 60fps |
| Video 暂停释放 | 101 MB | 176 MB | 降低 | — |
| Web（估算） | 220–350 MB | 150–250 MB | 100–200 MB | 视页面 |

## 7. 目录结构与代码规范

详见本文件第 8 节；当前仓库：

```text
samples/
  WallerpaperShell/          WinUI 3 UI
  WallerpaperHost/           调度器
  SceneWallpaperHost/        D3D11 场景
  VideoWallpaperHost/        libmpv
  WebWallpaperHost/          WebView2
  Shared/
    WorkerWRuntime/          桌面挂靠
    Wallerpaper.Diagnostics/ 日志 + dump
  Installer/
    WallerpaperSetup/        单文件安装器
tools/
  build-release.ps1
  ui-flow-test.ps1
docs/
  PRODUCTIZATION.md
  COMPATIBILITY-CHECKLIST.md
  RESOURCE-METRICS.md
  PHASE6-TEST-REPORT.md
artifacts/
  publish/
  WallerpaperSetup.exe
```

### 代码规范

- `Nullable=enable`、`LangVersion=latest`
- UI 线程不做文件/网络/编码；耗时操作 `Task.Run`
- 所有 Win32 句柄/COM/Process 必须 `Dispose`
- 子进程统一 stdio JSON 协议；未知命令返回 `E_CMD`
- 所有进程启动时安装 `AppLog` + `CrashReporter`
- P/Invoke 集中在 `NativeMethods`/局部 `DllImport`，写清 Win32 错误
- 配置 JSON 原子写：`.tmp` + `File.Move(overwrite)`
- 命名：`PascalCase` 类型/方法，`_camelCase` 字段，异步方法 `Async`
- 提交前：`dotnet build Wallerpaper.sln -c Release`、UI 流程脚本、日志/dump 检查

## 8. 发布检查清单

- [ ] `tools/build-release.ps1` 成功
- [ ] `artifacts/WallerpaperSetup.exe` 生成
- [ ] 干净用户账号安装成功
- [ ] 安装后桌面/开始菜单快捷方式存在
- [ ] HKCU Run 指向安装目录
- [ ] 重启登录后 `--minimized` 启动
- [ ] 卸载后无目录/注册表/快捷方式/数据残留
- [ ] 日志和 dump 可生成
- [ ] Win10 1809 / Win11 实机冒烟
- [ ] 100%/125%/150%/200% DPI 冒烟
- [ ] iGPU/dGPU 笔记本冒烟
- [ ] 代码签名证书（发布前）
