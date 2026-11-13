# VideoWallpaperHost（阶段 2：视频壁纸渲染子进程）

## 结构

```text
VideoWallpaperParent.exe
  └─ 启动 → VideoWallpaperHost.exe（独立子进程）
       ├─ WorkerWWindow：创建窗口并挂靠 WorkerW/Progman
       ├─ LibMpv：libmpv + D3D11VA/DXVA2 + wid 嵌入
       └─ stdio JSON lines：play / pause / volume / speed / fps / status / stop

主进程 stdout ← 子进程事件（ready/paused/playing/stats/error/stopped）
主进程 stdin  → 子进程命令
子进程 stderr → 日志（不污染 JSON）
```

源码：

- 子进程入口：[`Program.cs`](Program.cs)
- 窗口/WorkerW（共享）：[`../Shared/WorkerWRuntime/WorkerWWindow.cs`](../Shared/WorkerWRuntime/WorkerWWindow.cs)
- libmpv 绑定：[`LibMpv.cs`](LibMpv.cs)
- 主进程示例：[`../VideoWallpaperParent/Program.cs`](../VideoWallpaperParent/Program.cs)

## 官方 libmpv 来源

- 版本：`mpv-dev-x86_64-20261018-git-01e2533f31.7z`（x86_64）
- 下载：<https://sourceforge.net/projects/mpv-player-windows/files/libmpv/mpv-dev-x86_64-20261018-git-01e2533f31.7z/download>
- 包 MD5：`5ca2e48b297f05367eb355ab1e068284`（已校验）
- `libmpv-2.dll`：121,043,968 bytes，SHA-256 `82BAE3B6BF7BB737AFC9B52D590B477983C715F3C63C1A361F9EE06523DC5DC2`
- 位置：`samples/VideoWallpaperHost/libmpv-2.dll`（构建时由 csproj 复制到输出）
- 该官方构建是静态链接的，实测不需要额外复制 `msvcp140*.dll`/`vcruntime140*.dll`。
- 许可证：mpv/FFmpeg 为 GPLv2+ 系；本项目 GPL-3.0；分发时必须提供对应源码/许可与 NOTICE。

## 前置

- Windows 10 1809+ / Windows 11，x64。
- .NET 8 SDK。
- 官方 `libmpv-2.dll` 已在项目根目录；普通用户完整性（管理员/低完整性沙箱下 `SetParent` 会 `ACCESS_DENIED`）。
- 关闭覆盖桌面的全屏浏览器/游戏，否则看不到视频窗口。

## 构建/运行

```powershell
cd "D:\Desktop\wallerpaper c\samples\VideoWallpaperHost"
dotnet build -c Release
# csproj 会把项目根目录的 libmpv-2.dll 复制到 bin\Release\net8.0-windows\

cd "..\VideoWallpaperParent"
dotnet build -c Release
# 主进程默认找同目录 VideoWallpaperHost.exe；也可以显式指定：
dotnet run -- --child "..\VideoWallpaperHost\bin\Release\net8.0-windows\VideoWallpaperHost.exe" `
  --video "D:\videos\wallpaper.mp4" --fps 60 --volume 0
```

交互命令：`p` 播放、`s` 暂停并释放解码器、`r` 恢复、`+/-` 音量、`1/2/0` 速度、`f` 切换 30/60、`i` 状态、`q` 停止。

自动测试：`--auto-test`（暂停→状态→恢复→状态→停止）。

## IPC 协议

每行一个 JSON：

| 方向 | 消息 | 示例 |
|---|---|---|
| 父→子 | 播放 | `{"cmd":"play"}` |
| 父→子 | 暂停（默认释放解码器） | `{"cmd":"pause","release":true}` |
| 父→子 | 音量 0–100 | `{"cmd":"volume","value":35}` |
| 父→子 | 速度 0.25–4 | `{"cmd":"speed","value":1.5}` |
| 父→子 | FPS 30/60 | `{"cmd":"fps","value":30}` |
| 父→子 | 状态 | `{"cmd":"status"}` |
| 父→子 | 停止 | `{"cmd":"stop"}` |
| 子→父 | 就绪 | `{"ev":"ready","hwdec":"d3d11va","width":1920,"height":1080,...}` |
| 子→父 | 暂停 | `{"ev":"paused","released":true}` |
| 子→父 | 统计 | `{"ev":"stats","workingSetMB":...,"hwdec":"d3d11va",...}` |
| 子→父 | 错误 | `{"ev":"error","code":"E_HWDEC","message":"..."}` |
| 子→父 | 停止 | `{"ev":"stopped","exitCode":0}` |

退出码：

| 码 | 含义 |
|---|---|
| 0 | 正常停止 / 父进程关闭 stdin |
| 2 | 参数/视频文件不存在 |
| 3 | WorkerW/窗口失败或被 Explorer 销毁（父进程应重启子进程） |
| 4 | libmpv 初始化或硬件解码不可用 |
| 5 | 视频加载/解码失败 |

## libmpv 硬解关键参数

| 参数 | 作用 | 说明 |
|---|---|---|
| `wid=<HWND>` | 把视频输出嵌入 WorkerW 子窗口 | 必须在 `mpv_initialize` 前设置 |
| `vo=gpu` | GPU 视频输出 | `gpu-next` 可选，但 `gpu` 更省/更稳 |
| `gpu-api=d3d11` | D3D11 渲染后端 | Windows 8+ 首选 |
| `hwdec=d3d11va` | D3D11VA 硬解 | 首选；`hwdec-current` 验证 |
| `hwdec=dxva2` | DXVA2 硬解 | 老驱动/老系统回退 |
| `hwdec-codecs=all` | 所有可硬解 codec 都尝试 | 兼容性优先 |
| `hwdec-current` | 运行时查询实际硬解 | `d3d11va`/`dxva2`/空 = 未启用 |
| `keepaspect=yes` + `panscan=1.0` | cover 填充不变形 | `fit=contain` 时 `panscan=0.0` |
| `loop-file=inf` | 循环 | 本地壁纸 |
| `keep-open=yes` | 文件结束保持窗口 | 配合循环 |
| `idle=yes` | 无文件时保持实例 | 便于暂停/恢复 |
| `demuxer-max-bytes` / `demuxer-max-back-bytes` | 限制 demux cache | 本示例 16 MiB / 4 MiB |
| `vd-lavc-threads=4` | 限制解码线程 | 降低线程开销 |
| `scale/cscale/dscale=bilinear` | 低成本缩放 | 1080p 壁纸足够 |
| `video-sync=audio` 或 `display-vdrop` | 音画同步/丢帧 | 视 FPS 策略 |
| `config=no` / `load-scripts=no` | 不加载用户配置/脚本 | 可复现、安全 |
| `terminal=no` | 不向 stdout 写日志 | 保护 JSON IPC |

**强制硬解**：默认尝试 `d3d11va` → `dxva2`；两者都失败则退出码 4。只有显式加 `--allow-software` 才允许 `auto-safe` 软件回退。实际是否硬解以 `hwdec-current` 为准，不能只看 `hwdec` 参数。

## 性能/内存优化点

1. `wid` 直接嵌入，不做 CPU 帧拷贝；不要用 `hwdec=auto-copy`。
2. `d3d11va` 直接输出 D3D11 纹理；避免 `--hwdec=dxva2-copy`。
3. `scale/cscale/dscale=bilinear`，关闭 `correct-downscaling`；1080p 不追求 4K 放大画质。
4. `demuxer-max-bytes=16 MiB`、`demuxer-max-back-bytes=4 MiB`、`demuxer-readahead-secs=1`；本地循环视频不需要大缓存。
5. `vd-lavc-threads=4`、`audio-buffer=0.2`；限制线程/音频缓冲。
6. 音量为 0 时 `aid=no`，释放音频解码器；音量恢复时 `aid=auto`。
7. 暂停：`pause=yes` + `vid=no`，官方构建实测 working set 从约 156 MB 降到约 101 MB，`hwdec` 变为空（解码器释放）。恢复：`vid=auto` + `seek` + `pause=no`，working set 约 168 MB。
8. 停止：`mpv_terminate_destroy` → 销毁 WorkerW 子窗口 → 退出进程；显存由驱动/进程退出释放。
9. FPS：官方 libmpv 2.6 不支持 `max-fps`/`display-fps`，但支持 `vf` 滤镜；用 `@wp_fps:fps=30/60`，运行时 `vf remove @wp_fps` + `vf add`，避免滤镜堆叠。
10. 4K/10-bit/HDR 会显著增加显存；1080p 目标下 `1080p H.264/HEVC 8-bit + d3d11va` 最稳。

## 实测结果（官方 libmpv 20261018）

环境：Windows 11 26100，DPI 120（125%），主屏 1920×1200，测试视频 1080×1652 H.264，client API 2.6。

```text
READY hwdec=d3d11va size=1080x1652 duration=1.02s fpsCap=True
speed=1.25 / volume=0 / fps cap=60 均成功
stats: ws=156.0MB private=248.3MB hwdec=d3d11va fps=60.0
pause: released=True, ws=100.8MB private=175.9MB hwdec=""
resume: ws=168.0MB private=325.5MB hwdec=d3d11va fps=60.0
stop: child exit=0，无残留进程
损坏视频: E_DECODE / exit=5
```

FPS 切换使用：

```text
vf remove @wp_fps
vf add @wp_fps:fps=30|60
```

## 常见错误：SetParent err=5 / “Failed to find/attach WorkerW”

先看子进程 stderr 的新日志：

```text
process: ... privileged=False ... explorerProcesses=0
visibleProcesses=7
whoami: Mandatory Label\Low Mandatory Level ... S-1-16-4096
...
Attach attempt 0: SetParent(old=0x0, err=5) -> parentAfter=0x1000C, ...
```

`parentAfter=0x1000C` 是桌面窗口，说明 SetParent 被拒绝。按顺序排查：

0. **进程是 Low integrity（最常见且最容易被忽略）**  
   新日志会打印：

   ```text
   whoami: Mandatory Label\Low Mandatory Level ... S-1-16-4096
   visibleProcesses=7
   E_INTEGRITY
   ```

   `privileged=False` 只表示“没有提权”，**不代表是 Medium**。  
   Low integrity（`S-1-16-4096`）无法看到/访问 Medium 的 Explorer，`SetParent` 一定失败。

   在 PowerShell 里确认：

   ```powershell
   whoami /groups | findstr S-1-16
   ```

   - Medium：`S-1-16-8192`
   - Low：`S-1-16-4096`

   修复：不要从 DSH/沙箱/受限终端启动；从 Windows 桌面双击  
   [`run-wallpaper.cmd`](../../run-wallpaper.cmd) 或从桌面原生 PowerShell 运行。  
   如果双击脚本仍然是 Low，说明整个桌面/用户令牌被限制，需要重启/换正常账户。

   **更隐蔽的情况：PowerShell 是 Medium，但 exe 本身是 Low。**  
   即使从 Medium PowerShell 启动，Windows 也会把进程降为 exe 的 Low 标签：

   ```powershell
   icacls "...\VideoWallpaperHost.exe" | findstr "Mandatory Label"
   # Mandatory Label\Low Mandatory Level
   ```

   原因是 `bin`/`obj` 继承了低完整性工作区的标签。  
   [`run-wallpaper.ps1`](../../run-wallpaper.ps1) 会自动检测并重新构建到 Medium 用户目录：

   ```text
   C:\Users\legion\WallerpaperBuild\host\VideoWallpaperHost.exe
   C:\Users\legion\WallerpaperBuild\parent\VideoWallpaperParent.exe
   ```

   然后从该目录运行，不再受 Low 工作区标签影响。

1. **explorer.exe 没有运行，或当前不是交互式桌面**  
   `explorerProcesses=0` 时 WorkerW 不可用；这不是 libmpv 问题。  
   在正常桌面登录，或启动/重启 Explorer：

   ```powershell
   Get-Process explorer
   # 正常应有 1 个以上；没有就重启资源管理器/重启系统
   ```

   远程/无头/服务会话、只有终端没有桌面 shell 的环境，无法挂 WorkerW。

2. **父进程/子进程被“以管理员身份运行”**  
   子进程日志 `privileged=True` 表示提权；Explorer 通常是中完整性，提权进程 `SetParent` 会 `ACCESS_DENIED`。  
   用**非管理员** PowerShell 重新运行。

3. **Explorer 本身被提权，或与子进程完整性不同**  
   重启 Explorer 为普通用户；不要用管理员身份启动 explorer.exe。

4. **旧版本日志里的 `SetParent failed: 0` 不可靠**  
   旧代码在 `GetParent` 之后才读 `GetLastWin32Error`，错误被覆盖成 0。新代码在 `SetParent` 后立刻读，显示 `err=5`。

5. **父进程已经退出，`i` 被输入到 PowerShell**  
   `i` 是 VideoWallpaperParent 的交互命令，只能在 `READY` 之后、父进程仍运行时输入。  
   截图里子进程已 exit=3、父进程已退出，所以 PowerShell 把 `i` 当成命令，报 `CommandNotFoundException` 是正常现象。

修复环境后重新构建并运行，成功时应看到：

```text
Attach attempt 0: ... -> parentAfter=0x10172, expected=0x10172 ...
Attached: hwnd=..., parent=0x10172 ...
[child] READY hwdec=d3d11va ... fpsCap=True
```

## 已知问题

### 1. FPS 锁定依赖 `vf` 滤镜

当前官方构建（client API 2.6）：
- `max-fps`、`display-fps` 不支持；
- `vf` 滤镜可用，`fpsCap=true`；
- 代码使用命名滤镜 `@wp_fps:fps=30/60`，切换时先 remove 再 add。

如果换成不支持 `vf` 的精简 libmpv，`fpsCap` 会变成 false；此时只能：
- 换官方构建；
- 或改用 render API + 自己的交换链做呈现限帧（不在本阶段范围）。

### 2. 暂停释放的视觉表现

`vid=no` 会释放解码器，但某些 VO/驱动会清空最后一帧（黑屏）。如果产品要求“暂停时保留最后一帧”，需要在 pause 前截图并叠加一个静态图层，或改用 `stop` + 静态快照兜底；本阶段只验证资源释放。

### 3. 显存统计

本示例的 `stats` 只统计进程 working set/private bytes，不统计 VRAM；libmpv 的 D3D11 设备不暴露给宿主。VRAM 请用：
- 任务管理器 → 性能 → GPU → 专用 GPU 内存；
- GPU-Z / PresentMon；
- 或后续用 `IDXGIAdapter3::QueryVideoMemoryInfo` 做独立遥测。

官方 libmpv 2.6、1080×1652 H.264、d3d11va、fps=60：
- 播放：working set ≈ 156 MB；暂停释放后 ≈ 101 MB；恢复后 ≈ 168 MB。
- private bytes ≈ 248–326 MB（private 包含 .NET/FFmpeg 的提交内存，不等同于常驻内存）。
- 1080p 横向视频通常更低；4K/10-bit/HDR 会明显更高。

### 4. 显卡硬解兼容性

| GPU | 建议 | 说明 |
|---|---|---|
| NVIDIA GTX 10xx/RTX | `d3d11va` | NVDEC 经 D3D11VA；老卡可 `dxva2`；Optimus 双显卡注意输出适配器 |
| AMD GCN/RDNA | `d3d11va` | 老 GCN 可 `dxva2`；10-bit HEVC/AV1 看代际 |
| Intel HD/UHD/Iris/Xe | `d3d11va` | Quick Sync；老核显可能只支持 DXVA2/8-bit |
| AV1 | RTX 30+/RX 6000+/Arc/11代+ | 老卡只能软解；强制硬解会退出码 4 |
| HEVC 10-bit | 6代 Intel+/GTX 10+/GCN 4+ | 驱动/代际差异大；以 `hwdec-current` 为准 |

混合显卡：mpv 默认可能选错适配器。可尝试 `--d3d11-adapter`（不同构建支持不同）或在 Windows 图形设置里把 `VideoWallpaperHost.exe` 设为“高性能”。如果输出在 iGPU 而解码在 dGPU，会出现拷贝开销/黑屏，需要实机验证。

### 5. 其他

- WorkerW/raised desktop 的兼容性沿用阶段 1；本子进程在窗口丢失时退出码 3，由父进程重启，不在子进程内重挂。
- `--seconds` 只用于 demo；生产环境由父进程发 `stop`。
- 本示例不包含音频可视化、HDR tone mapping、多显示器；后续阶段扩展。
- GPL-3.0：libmpv/FFmpeg 的源码与许可需随分发提供；不要把本示例的二进制直接当作合规分发包。
