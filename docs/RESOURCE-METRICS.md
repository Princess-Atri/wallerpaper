# Wallerpaper 资源占用指标

环境：Windows 11 26100，1920×1200，125% DPI，单显示器。  
数据来源：本仓库阶段 3–6 实测 + 本次 Shell 闲置测量。

## 1. 汇总

| 场景 | 工作集 | 私有内存 | VRAM | CPU | GPU |
|---|---:|---:|---:|---:|---:|
| Shell 闲置（主窗口） | 162.6 MB | 98.2 MB | 少量 | ~1.0% CPU 累计 | — |
| Scene `audio_gradient` 1920×1200 | 84–95 MB | 78–87 MB | 16.7 MB | 0.4–0.8% | 0.1 ms |
| Video 1080×1652 H.264 d3d11va 播放 | 156 MB | 248 MB | 未暴露 | — | 60 fps |
| Video 暂停/释放解码器 | 101 MB | 176 MB | 降低 | — | — |
| Video 恢复 | 168 MB | 326 MB | 恢复 | — | 60 fps |
| Web 基础页面（估算） | 220–350 MB 总计 | 150–250 MB | 100–200 MB | 视页面 | 视页面 |

> Web 是 WebView2 多进程模型，任务管理器里应把 `WebWallpaperHost.exe` 和
> `msedgewebview2.exe` 子进程合计；本表为工程估算，未做逐进程实测。
> Video 的 private bytes 包含 mpv/FFmpeg 提交内存，不等于常驻物理内存。

## 2. Scene 实测

`audio_gradient`：4 层、220 粒子、2048 贴图上限、60 FPS。

```text
stats: fps=60.0
workingSetMb=84.5–94.8
privateMb=77.6–87.1
vramUsedMb=16.7
gpuMs=0.1
cpuPercent=0.4–0.8
scriptMs=0.22
drawMs=0.04
presentMs=0.27
```

结论：

- 基础场景非常轻量，CPU/GPU 接近空载。
- 主要成本在贴图和粒子；BC3 压缩后 VRAM 明显下降。
- 场景不作为闲置资源瓶颈。

## 3. Video 实测

官方 libmpv 2.6、1080×1652 H.264、d3d11va、60 FPS：

| 状态 | Working Set | Private |
|---|---:|---:|
| 播放 | 156 MB | 248 MB |
| 暂停 + 释放解码器 | 101 MB | 176 MB |
| 恢复 | 168 MB | 326 MB |

暂停策略确实释放约 55 MB 工作集；恢复后 private 上升是 FFmpeg/mpv 缓存。

VRAM 未由 libmpv 暴露；用任务管理器“专用 GPU 内存”观察。

## 4. Web 估算基线

WebView2 典型：

- Host 进程：30–60 MB
- 1 个渲染进程 + GPU 进程：150–250 MB
- 1080p 动画页面 VRAM：100–200 MB
- 多 Web 壁纸会叠加；当前 Shell 限制同时运行数量。

建议验收时：

```powershell
Get-Process WebWallpaperHost,msedgewebview2 |
  Measure-Object WorkingSet64 -Sum
```

## 5. 闲置验收

- Shell 主窗口闲置：< 300 MB WS，< 1% CPU。
- 壁纸暂停/stop 策略：Host + 渲染子进程应释放或降 CPU。
- 全屏暂停：Scene/Video CPU 接近 0，GPU 下降。
- 多显示器：每屏一个子进程，内存线性增长；需要按屏预算。
- 8h/24h 观察 handle/private 增长；已有 60s soak 无确认泄漏。

## 6. 优化建议

1. Shell 启动时不要加载所有缩略图；按分类懒加载。
2. Web 壁纸限制同时运行数，闲置自动 suspend。
3. Video 暂停时释放解码器（现有）。
4. Scene 贴图 BC3/BC7 + LRU（LRU 尚未实现）。
5. 监控 `maxTextureMemoryMb` 与实际 VRAM，超限降 renderScale。
6. 发布版关闭调试日志的逐帧 stats（仅按需开启）。
