# 阶段 2 视频壁纸测试报告

测试时间：2026-10-26 17:22–18:18（Asia/Shanghai）  
环境：Windows 11 26100，DPI 125%，1920×1200，官方 libmpv 2.6，D3D11VA，Medium integrity，Explorer WorkerW 正常。

## 重要修正：第一次 10 分钟测试是暂停状态

第一次 10 分钟测试中，`stats.time` 始终是 `0.00`，CPU 仅 0.18% 单核，用户也看不到画面变化。

原因是：子进程初始化时是 `pause=yes`，而父进程的 `--duration` 模式只发送 `status`，从未发送 `play`。所以那次测试测的是**暂停画面**，数据无效。

已修复：

- 子进程加载完成后自动 `Play()`；
- 父进程 duration 模式也显式发送 `play`；
- 日志新增 `autoplay: video started`；
- 验证到 `time-pos` 随时间前进，视频确实在动。

第一次无效数据保留在 [stability-10min-paused-invalid.log](../stability-10min-paused-invalid.log)。

## 测试对象

- 视频：`D:\videos\wallerpaper.mp4`
- 规格：1920×1080，H.264，10.0s 循环，源 30fps
- 目标：60fps cap
- 音量 0（`aid=no`）
- 子进程：`C:\Users\legion\WallerpaperBuild\host\VideoWallpaperHost.exe`

## 1. 有效的 10 分钟播放测试

### A. cache=16MiB + fps 滤镜

- 样本：41 个，每 15 秒
- 工作集：159.9 → 184.1 MB（**+24.2 MB**，斜率 +1.864 MB/min）
- 工作集均值/峰值：176.7 / 184.1 MB
- Private：268.0 → 286.1 MB（**+18.1 MB**，斜率 +1.468 MB/min）
- 硬解：`d3d11va` 41/41
- FPS：60
- 日志：[stability-10min-cache16-vf60.log](../stability-10min-cache16-vf60.log)

### B. cache=8MiB + fps 滤镜

- 工作集：161.8 → 183.2 MB（**+21.4 MB**）
- Private：270.1 → 285.1 MB（**+15.0 MB**）
- FPS：60
- 结论：降低缓存后增长略小，但没有消除。

### C. cache=8MiB + 不复制帧（30fps 源保持 30fps）

- 工作集：161.5 → 182.5 MB（**+21.0 MB**），峰值 186.5 MB
- Private：268.5 → 286.1 MB（**+17.6 MB**）
- CPU：66.09s / 10min ≈ **11.0% 单核**
- Threads：91–100；Handles：758–785
- FPS：30（因为 30fps 源不再复制成 60）
- 日志：[stability-10min.log](../stability-10min.log)

### 结论

1. **之前“无泄漏、+4.9MB”的结论无效**，因为那次是暂停状态。
2. 真实播放 10 分钟后，工作集约 **183–186MB**，仍低于 200MB，但呈缓慢上升趋势（约 2MB/min）。
3. Cache 从 16MiB 降到 8MiB、以及去掉 fps 复制滤镜，都没有消除增长；增长更像**每次 loop 的 mpv/D3D11 资源累积**，而不是 demux cache 或 fps 滤镜。
4. 10 分钟尚不足以判断是否会长期无界增长；需要 30–60 分钟或 24 小时测试。
5. 最终保留 fps 滤镜（60fps 目标，30fps 源复制到 60），因为去掉滤镜没有内存收益，反而降低输出帧率。

## 2. 损坏视频异常测试

4KB 随机字节，扩展名 `.mp4`：

```text
mpv END_FILE reason=4 error=-17: unrecognized file format
hwdec attempt 'd3d11va' failed: ...
format/container error: skip remaining hwdec fallbacks.
ERROR E_DECODE: mpv failed to load video: Failed to recognize file format.
STOPPED exit=5
```

结果：

- 子进程不崩溃，清理窗口/进程，退出码 **5**。
- 父进程不崩溃；第一次发现父进程把 5 映射成 4，已修复为传播 `_exitCode`。
- 格式错误不再尝试第二个硬解后端，只初始化一次 mpv。
- 无孤儿进程。
- 日志：[corrupt-test.log](../corrupt-test.log)

## 3. 已实施的迭代

1. **自动播放修复**：解决“视频不动”和暂停状态下的无效稳定性数据。
2. **父进程退出码传播修复**：5 不再变成 4。
3. **损坏视频快速失败**：格式错误不再 dxva2 重试。
4. **长跑测试能力**：`--duration` / `--sample`，子进程 stats 增加 CPU/线程/句柄/cache。
5. **缓存调优实验**：16MiB/4MiB → 8MiB/2MiB，readahead 0.5s；记录对比数据。
6. **fps 语义实验**：尝试“目标是上限、不复制 30fps 帧”；因无内存收益且降低输出帧率，最终改回 fps 滤镜。

## 4. 仍存在的问题

1. **长跑内存缓慢上升**：10 分钟 +21～24MB，尚未定位到单一配置项；需要更长测试和 VRAM 遥测。
2. **VRAM 未实测**：当前无 GPU 显存采样。
3. **Private bytes 约 286MB**：高于 200MB；working set 仍在 200MB 内。
4. **仅验证 1080p H.264**；4K/HEVC 10-bit/AV1/HDR 未测。
5. **30fps 源 + 60fps 目标使用 fps 滤镜复制帧**；如果更在意 CPU/GPU，可改回“只做上限、不复制”。

## 5. 后续建议

1. 跑 30–60 分钟长跑，确认工作集是否会突破 200MB 或趋于平台。
2. 用 GPU-Z/PresentMon/`IDXGIAdapter3` 采 VRAM；区分 WDDM 驱动占用与进程私有内存。
3. 做 loop 对照实验：同一视频不循环播放 10 分钟，判断增长是否来自每次 loop 的渲染资源。
4. 若确认 per-loop 增长，考虑子进程定期重启（例如每 30–60 分钟）作为兜底，并记录重启耗时/闪屏。
5. 增加 4K/HEVC/AV1 测试矩阵。

## 复现命令

```powershell
# 有效 10 分钟稳定性
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\Desktop\wallerpaper c\run-wallpaper.ps1" `
  -Video "D:\videos\wallerpaper.mp4" -DurationSeconds 600 -SampleSeconds 15 -Fps 60

# 损坏视频
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\Desktop\wallerpaper c\run-wallpaper.ps1" `
  -Video "D:\Desktop\wallerpaper c\samples\VideoWallpaperHost\corrupt-test.mp4" `
  -DurationSeconds 1 -SampleSeconds 1 -Fps 30
```
