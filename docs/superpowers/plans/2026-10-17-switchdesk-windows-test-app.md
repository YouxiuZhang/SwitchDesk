# Windows Installable Switching Test App Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans. Steps use checkboxes for tracking.

**Goal:** 交付可双击安装、打开和测试真实显示器切源的 Windows 安装包，用户无需命令行。

**Architecture:** 根据用户“直接给我安装包”调整交付顺序，沿用已批准的本地视频切源方案和浅色紫色界面。Windows WinForms 客户端直接调用 Win32 DDC；同一 exe 的隔离工作进程承担原生读取/写入并受父进程超时限制。首次试切由独立守护进程限时尝试恢复，避免 UI 关闭后丢失恢复计时。先验证 Windows 控制显示器，不把键鼠、剪贴板和预览伪装成已完成。

**Tech Stack:** C# 5 / .NET Framework 4.8 / WinForms，微软引用程序集与现有 Roslyn 交叉编译，NSIS 当前用户安装。无用户端 PowerShell、开发 SDK 或全局配置修改。

---

## 授权与证据

原产品设计已批准；用户明确要求直接提供安装包并对反复脚本测试表示不满。此阶段不再要求运行脚本、人工采集报告或审批设计。当前 Windows 只验证了保持原连接时的 VCP 17 回读；用户尚未完成双机接屏。显示器真实双向切源仍需在安装版中测试。项目非 Git 仓，不 init/commit/push。CodeGraph 未初始化，不做结构性仓库搜索；只读取已知 A1 源文件。

## 本版交互

- 一个中文窗口，浅色背景/紫色主操作；自动枚举显示器和当前输入，选择实际显示器与 Mac 所接接口（HDMI1/HDMI2/DP）。不根据输入17自动宣称 Mac/Windows 映射已验证。
- 首次“试切到 Mac”：当前输入回读成功且目标不同，用户点击后才发 VCP 0x60 写入；界面说明倒计时后仅“尝试切回”，不能保证显示器一定接受。
- 独立守护进程须在发切源前启动成功，约10秒后以独立、受限工作进程尝试恢复原输入；UI 请求提前恢复时只通知守护进程，由它执行唯一一次恢复，防止重复切回。守护进程与 UI 不并发发写命令。无法确认旧工作进程已退出时停止后续写入。
- 试切后回读到原输入，用户确认确实见到 Mac 桌面再启用普通切换/Windows键盘 Ctrl+Alt+Space；Ctrl+Alt+Backspace 返回 Windows。快捷键注册失败明确显示，不吞掉报错。
- 明确标注“切屏测试版；键鼠共享、剪贴板共享、桌面预览尚未接入”。不显示假在线设备或假实时画面。
- 关闭窗口正常退出并释放快捷键；若仍在首次测试中先尝试恢复，无法等待时独立守护继续其已有一次恢复任务。无登录自启、网络服务或常驻注册。
- 简短错误、可展开/查看的本地测试记录与导出按钮，便于用户直接反馈。导出由用户选择路径，不上传，避免包含机器名/网络信息。

## Task 1：原生应用与最小必要测试

**Files:**
- Create: `native/windows/app/Core.cs`（输入候选、状态与规则）
- Create: `native/windows/app/MonitorControl.cs`（独立的 DDC 写入；不修改 A1 `Probe.cs`）
- Create: `native/windows/app/Program.cs`（GUI/工作进程入口、独立恢复与进程超时）
- Create: `native/windows/app/MainForm.cs`（中文 UI、快捷键、错误/状态）
- Create: `tests/test_windows_app.py`（核心规则和编译验证；实际 Win32 测试有环境才执行）

- [x] 先添加失败测试，核心规则验证：未选择/当前不可读/目标与原输入相同均不能开始试切；非试切成功与画面确认不能启用普通切换；不允许超出固定输入集合的写入。

```csharp
// Fixed interoperable VCP 0x60 choices; actual success requires hardware confirmation.
// HDMI1=0x11, HDMI2=0x12, DisplayPort=0x0F.
// Return/read statuses must not be conflated with visual success.
```

- [x] 用现有 `SwitchDesk.Native.Collect(true)` 完成只读发现；工作进程复用 A1 源码编译进同一应用，无用户端脚本。写入层独立枚举物理句柄，匹配 ID 与名称，拒绝选中目标消失/不匹配；调用 SetVCPFeature(...,0x60,value)，finally 释放句柄。只允许上述3个端口值，恢复允许启动时真实读取的原值（uint16），不得写其他VCP或调刷新率。
- [x] 每次原生操作在隔离子进程，有限等待与 kill/确认；stdout/stderr 同时异步读取，不无界 WaitForExit/Task.Result。创建失败/超时/无法清理/报告损坏用独立状态，不制造成功。原生 write 返回和另一次回读分别记录；回读失败显示“切换未确认”。
- [x] 独立恢复任务仅覆盖这次用户点击的首次试切，使用随机运行标识和专用临时路径（当前用户目录），时间有界，一次恢复尝试；不接受外部网络命令。检测显示器拓扑变化使当前映射失效，禁止在未知目标上继续自动写入。
- [x] GUI 不在 UI 线程做 DDC 调用；禁止重入/自动重复切换；选中信息的变更立即清除之前确认状态。原输入不假定17，选中的 Mac 输入不假定18。将“命令返回”“回读确认”“用户看见画面”分开处理。
- [x] 完成核心规则测试、编译及静态平台核查后，做一次独立需求审查，再做一次质量审查。修复实际问题，不重复扩充无关诊断框架。

## Task 2：交叉构建与真实安装包

**Files:**
- Create: `tools/build-windows-app.ps1`
- Create: `native/windows/app/SwitchDesk.exe.config`
- Create: `native/windows/installer/SwitchDesk.nsi`
- Create: `native/windows/app/README.zh-CN.md`
- Output: `dist/installer/SwitchDesk-Setup-0.1.0.exe`

- [x] 下载仅构建所需的官方 .NET Framework 4.8 引用程序集，复用已有 PowerShell 内 Roslyn 编译器。所有工具放 `.build/tools`，不安装全局环境。构建 WinExe，STAThread，版本0.1.0，架构x64或AnyCPU优先64位。
- [x] NSIS 使用当前用户目录，创建开始菜单和可选桌面快捷方式、卸载入口；不改执行策略/系统防护，不要求管理员。卸载只删除自己的已知文件和注册表键，不递归删除任意用户目录；卸载保留用户测试日志。
- [x] 安装时检测 .NET Framework 4.8；若缺失显示明确要求，不静默联网下载依赖。关闭安装中程序的处理给用户明确提示，不任意杀进程。
- [x] 编译真实 PE 应用与 NSIS PE 安装器，检查引用程序集、子系统、架构、版本、包内文件与源码一致、SHA256。本机未运行 Windows GUI 和 DDC 写入时明确记录，不报告实机通过。
- [x] 更新 README 和验证记录，交付安装包及“安装→选显示器/目标口→试切”的短说明。无需另发脚本，不把原诊断ZIP冒充安装包。
