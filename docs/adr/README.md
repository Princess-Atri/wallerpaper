# Architecture Decision Records（ADR）索引

> 状态：D1–D5 已确认；下列 ADR-001~013 标记为 **Accepted**，Phase 0 只做验证，不再重新选型。  
> 模板：背景 → 决策 → 备选 → 后果 → 验证方式。

| ID | 决策 | 状态 | 关联章节 |
|---|---|---|---|
| ADR-001 | 每个“壁纸实例 × 显示器”一个 RenderHost 进程 | Accepted | §1.2, §1.7 |
| ADR-002 | Host 控制面使用 stdin/stdout 匿名管道 + 长度前缀 MessagePack | Accepted | §1.8 |
| ADR-003 | 桌面挂靠由 Host 进程内执行，主进程只给策略/句柄 | Accepted | §1.9 |
| ADR-004 | 暂停采用分级释放 + 静态快照兜底 | Accepted | §1.11 |
| ADR-005 | UI 采用 WinUI 3，非打包 + Windows App SDK 自包含/安装器部署 | Accepted | §1.4, §4.1, §4.2 |
| ADR-006 | 目标 .NET 10 LTS，而不是 .NET 8 | Accepted | §4.1 |
| ADR-007 | 场景渲染采用 Vortice.Windows，Silk.NET 只作备选 | Accepted | §4.2 |
| ADR-008 | 视频采用 libmpv `wid` in-proc，`IVideoEngine` 保留 `mpv.exe` 兜底 | Accepted | §1.12.1, §4.2 |
| ADR-009 | 1.0 不支持应用型壁纸（exe） | Accepted | §0.3, §4.4 |
| ADR-010 | 不使用 `DebugActiveProcess`/全局钩子/DLL 注入 | Accepted | §1.13, §5.6 |
| ADR-011 | 项目许可证采用 GPL-3.0，可复用 Lively 代码并保留版权/来源 | Accepted | §0.1, §4.5 |
| ADR-012 | 1.0 范围包含视频 + Web + 场景三后端 | Accepted | §2.1, §4.5 |
| ADR-013 | 部署采用 .NET 10 + Windows App SDK 自包含/安装器 + Velopack | Accepted | §1.4, §2.7 |

## 待补（Phase 0 内定稿）

- ADR-014：静态壁纸默认策略（是否默认开启、Spotlight 处理）。
- ADR-015：WebView2 profile 策略（每壁纸独立 vs 共享）。
- ADR-016：`duplicate`/`span` 是否进 1.0。
- ADR-017：安装器/更新通道（Velopack stable/beta）。
- ADR-018：GPL 合规清单（Lively 复用范围、libmpv/FFmpeg 源码、NOTICE）。
