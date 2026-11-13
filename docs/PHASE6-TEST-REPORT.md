# 阶段 6 WinUI 3 用户流程测试报告

测试时间：2026-10-29  
环境：Windows 11，1920×1200，单显示器，.NET SDK 10.0.403，Windows App SDK 1.7  
测试进程：`C:\Users\legion\WallerpaperBuild\shell\WallerpaperShell.exe`

## 1. 工具链与项目

- WinUI 3 项目：[`samples/WallerpaperShell/WallerpaperShell.csproj`](../samples/WallerpaperShell/WallerpaperShell.csproj)
- 经典解决方案：[`Wallerpaper.sln`](../Wallerpaper.sln)
- 新式解决方案：[`Wallerpaper.slnx`](../Wallerpaper.slnx)

```text
dotnet build samples/WallerpaperShell/WallerpaperShell.csproj -c Release
→ 0 warnings / 0 errors
```

已找到 **Visual Studio Community 2026 18.11.2**：

```text
D:\visual studio2
MSBuild: D:\visual studio2\MSBuild\Current\Bin\MSBuild.exe
```

已安装：

```text
Microsoft.VisualStudio.Workload.ManagedDesktop
Microsoft.VisualStudio.Component.Windows11SDK.26100
```

使用 VS 2026 MSBuild 构建整个解决方案：

```text
MSBuild.exe Wallerpaper.sln /t:Build /p:Configuration=Release /p:Platform=x64
→ 0 errors
```

缺失：

```text
Microsoft.VisualStudio.ComponentGroup.WindowsAppSDK.Cs
```

已尝试通过 VS Installer 安装该组件，但 VS Installer 的 `--quiet` 修改要求进程一开始就是
elevated；当前会话不是 elevated，UAC 也无法在此自动化会话中确认（退出码 1）。
现有项目不依赖该模板组件即可用 MSBuild/CLI 构建；如需 VS 的 WinUI 项目模板/设计器，
在 Visual Studio Installer 中勾选 **WinUI application development**（或
**Windows App SDK C# Templates**）并确认 UAC 即可。

## 2. 用户流程

| 步骤 | 结果 |
|---|---|
| 启动主界面 | 0.61–0.89s 出现窗口 |
| 浏览视频分类 | 显示 `demo` 卡片和 Shell 视频缩略图 |
| 浏览场景分类 | 显示 `audio-gradient` 卡片 |
| 浏览 Web 分类 | 显示 `index`、`test-interaction` 卡片 |
| 选择视频 | 右侧属性面板回填音量/速度/着色器/目标显示器 |
| 应用壁纸 | `WallerpaperHost` + `VideoWallpaperHost` 启动 |
| 调音量 | 33，写入配置并发送 Host IPC |
| 调速度 | 1.25，写入配置并发送 Host IPC |
| 预览 | 独立 `预览 · demo` 窗口，视频播放，置顶 |
| 关闭主窗口 | 进程保持运行，托盘窗口存在 |
| 托盘菜单 | 显示：打开、暂停壁纸、快速切换壁纸、设置、退出 |
| 托盘暂停 | 主界面按钮文字切换为“播放” |
| 设置页 | 滚动到底部，修改 AC FPS=50，自启动=开，保存成功 |
| 重启程序 | 读取 acFps=50、autostart=true，Host/视频子进程自动启动 |
| 开机自启模拟 | `--minimized` 启动后主窗口不可见，Host/视频子进程启动 |
| 退出 | Shell、Host、渲染子进程全部退出 |

截图：

- [主界面/视频流程](../docs/assets/shell-flow-video.png)
- [属性面板](../docs/assets/shell-flow-properties.png)
- [预览窗口](../docs/assets/shell-flow-preview.png)
- [托盘菜单](../docs/assets/shell-flow-tray.png)
- [设置页](../docs/assets/shell-flow-settings.png)

## 3. 配置持久化

路径：

```text
%LOCALAPPDATA%\Wallerpaper\shell-config.json
```

测试后实际值：

```json
{
  "performance": { "acFps": 50 },
  "general": { "startWithWindows": true },
  "wallpapers": [
    { "title": "demo", "volume": 33, "speed": 1.25, "muted": false }
  ],
  "monitors": {
    "\\\\.\\DISPLAY1": { "wallpaperId": "..." }
  }
}
```

重启 Shell 后：

```text
relaunch loaded: acFps=50 autostart=True
relaunch host processes: VideoWallpaperHost,WallerpaperHost
```

## 4. 自启动

注册表：

```text
HKCU\Software\Microsoft\Windows\CurrentVersion\Run
WallerpaperShell = "C:\Users\legion\WallerpaperBuild\shell\WallerpaperShell.exe" --minimized
```

模拟登录启动：

```text
autostart simulation: pid=..., tray=0x..., mainVisible=False
autostart simulation host processes: VideoWallpaperHost,WallerpaperHost
```

**没有真正重启电脑**（会中断当前会话）；用 `--minimized` + 注册表验证了登录启动路径。
注册表与 `general.startWithWindows=true` 当前保持开启，下一次真实登录会生效。

## 5. 缩略图

缓存目录：

```text
%LOCALAPPDATA%\Wallerpaper\cache
```

测试后 4 个 PNG：

```text
0dd6eec060a5_480x270.png  27918
82c2b874530e_480x270.png 298700
a5663f2efb47_480x270.png  26823
d16f737adb6b_480x270.png 114410
```

- 视频：Windows Shell `IShellItemImageFactory` 缩略图
- 场景：`scene.json` 第一张 image 贴图
- Web：确定性占位图
- 卡片 Automation Name 已改为壁纸标题，不再暴露 `Wallerpaper.Shell.Models.WallpaperItem`

## 6. 交互问题与调整

| 问题 | 调整 |
|---|---|
| 缩略图卡片自动化名是类名，读屏/自动化不可用 | 卡片增加 `AutomationProperties.Name=Title` |
| Slider 没有可访问名称 | 音量/速度/着色器 Slider 增加 `AutomationProperties.Name` |
| 预览窗口被主窗口/其他窗口挡住 | 预览调用 `SetWindowPos(HWND_TOPMOST)` + `Activate` |
| 状态栏直接显示 host/mpv 原始日志 | 只显示“调度器已启动/音量已应用/…”等友好文本 |
| 暂停图标渲染成 `00` | 改成明确的 `II` |
| `--minimized` 只隐藏窗口，不启动调度器 | `RefreshCatalogAsync` 完成后启动 Host |
| 保存设置后重启 Host，容易误以为设置没生效 | 状态栏显示“设置已保存，调度器已重启” |
| 属性回填 Slider 误触发保存 | 按“值是否变化”判断，而不是全局 suppress 标志 |

## 7. 验收结论

- 日常使用全流程：**通过**
- 启动/切换/设置操作无明显卡顿：**通过**
- 配置重启不丢失：**通过**
- 托盘菜单/预览/设置面板：**通过**
- 真实双屏/热插拔：**未验证（单屏环境）**
- 真实重启电脑：**未执行，使用登录启动模拟**

## 8. 剩余风险

1. 多显示器分配只做了单屏验证。
2. Web 缩略图是占位图。
3. 场景预览是缩略图，不是 GPU 实时预览。
4. 没有 MSIX/代码签名/自动更新。
5. VS 2026 的 WinUI C# Templates 组件未安装（UAC 无法自动确认）；MSBuild 构建通过，但未验证 VS XAML 设计器。
6. 托盘菜单通过隐藏窗口的 `WM_COMMAND` 回调测试，未做真实鼠标坐标点击。
