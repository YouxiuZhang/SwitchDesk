# 2026-10-17 原生诊断验证记录

## 验证范围

本记录覆盖阶段 A1 的只读诊断工具，以及 A2a 的限时输入观察工具。它不代表完整产品已经可用，也不证明自动切屏、键鼠共享、原生文件粘贴或另一台电脑预览已实现。

## Mac 实机结果

在当前 Apple Silicon Mac 上构建并运行原生探针，报告保存在本地 `reports/macos-current.json`。报告采集时间为 `2026-10-17T05:55:22.380Z`，系统版本为 macOS 26.5.1（25F80），架构 arm64。

修复写入失败清理后，重新构建并于 `2026-10-17T06:05:44.045Z` 采集 `reports/macos-verified.json`，再次识别同样的两块显示器，报告校验通过、warnings 为空。

| 显示器 | 类型 | 当前模式像素 | 刷新率 | DDC 输入回读 |
| --- | --- | --- | --- | --- |
| 27G2G4 | 外接 | 1920 × 1080 | 144 Hz | 未测试 |
| Built-in Retina Display | 内置 | 3024 × 1964 | 120 Hz | 不适用 |

报告校验通过，warnings 为空。当前诊断进程的屏幕采集权限预检为 `granted`，输入监听与输入注入为 `denied`，后者仅表示当前进程未获授权，不代表用户曾拒绝。没有触发授权申请，也没有读取画面或按键。将来的安装版应用必须单独确认自身权限。

沙箱内运行只能获得空显示器列表及一条警告；沙箱外只读运行成功枚举两屏。两种报告均符合契约，空列表不被解释为“没有连接显示器”。

## 自动化验证

A1 交付时重新执行 `SWITCHDESK_PWSH="$PWD/.build/tools/powershell-7.6.6/pwsh" python3 -m unittest discover -s tests -v`：35 项用例中，29 项通过，6 项 Windows 专属用例跳过，无失败。以下通过数量不包含跳过项。

- 报告验证器：18 项测试通过，覆盖字段契约、未验证能力、异常 JSON / Unicode、只读 CLI 和路径含空格。
- Mac CLI：7 项测试通过，覆盖帮助、参数错误、合法报告、UTF-8 文件输出、拒绝覆盖、父目录不存在和写入失败后无半成品残留。
- Mac 构建产物为 arm64，最低系统版本 macOS 13.0；当前实际运行环境为上述 macOS 26.5.1。
- `codesign` 检查显示二进制带链接器生成的 ad-hoc 签名，未设置 Team ID；没有 Developer ID 发布签名，也未公证。

## Windows 验证边界

用户现已提供一次 Windows 实机报告，采集时间为 `2026-10-17T06:34:57.453Z`，原内容保存在 `reports/windows-2026-10-17-ddc.json`，通过 `probe_report.py` 校验。

| 项目 | 用户提供的实测值 |
| --- | --- |
| 系统 / 架构 | 10.0.26200.9457 / x64 |
| 枚举到的物理显示器 | 1 台，名称 Generic PnP Monitor |
| 当前画面模式 | 1920 × 1080，60 Hz |
| VCP 0x60 | readable，值 17 |
| warnings | 空数组 |

这份报告提供了一次 Windows 枚举和 DDC 输入回读成功的证据。通用显示器名称本身不能证明型号，PowerShell 版本也不在报告中。输入值与主机的实际对应、切到另一输入后的可读性、软件切源/切回和时延均未验证。不能把 60 Hz 当作显示器或视频线的最高刷新率。

本地准备了微软官方 PowerShell 7.6.6 arm64 便携运行时，仅供源代码检查，位于忽略目录 `.build/tools/powershell-7.6.6`；没有全局安装或修改执行策略。来源为 [PowerShell 官方发布](https://github.com/PowerShell/PowerShell/releases/tag/v7.6.6)，下载包 SHA-256 已核对：`6df833d094ebac1c1a74340d7b3437f4aaf5e03ce640484a1c4359f3ce8b3db1`。

已通过 PowerShell 语法、C# 5 编译/Win32 结构体布局、物理显示器名称处理和非 Windows 平台保护这 4 项便携检查。结构体按官方布局核对：`MONITORINFOEXW` 104 字节、`DEVMODEW` 220 字节、64 位 `PHYSICAL_MONITOR` 264 字节。

以下 6 项自动化测试在 Mac 明确跳过，不能因收到一次用户报告而全部计为通过：Windows 默认枚举与报告、显式 DDC 回读、UTF-8 文件与拒绝覆盖、同进程连续运行、PowerShell `Set-Location` 后的相对路径、父目录不存在。用户提供的硬件证据与自动化测试结果分别记录。

## 本地交付文件

Windows 包 `dist/switchdesk-windows-probe.zip` 仅包含 `Probe.cs`、`diagnose.ps1`、`README.zh-CN.md` 三个文件。已核对 ZIP CRC、文件清单和每个文件的内容与源码一致，不包含报告、第三方运行时或本机信息。

| 文件 | SHA-256 |
| --- | --- |
| `switchdesk-windows-probe.zip` | `1f558b20bf246f87b4ff0db2edb68de17387ec131d4da2f0f08696bd518e47dd` |
| `macos/switchdesk-probe` | `81451ca3630501f663ac2dfadf6dd4a46e3260515a26ee61fc09cd02c4c5dc4b` |

校验值同时保存在 `dist/SHA256SUMS.txt`。诊断源码和产物留在当前工作区；未创建 Git 仓库、提交或推送。三个代码任务均通过独立需求与质量审查，最终集成交付审查无阻塞问题。

## A2a：限时输入观察

新增 `native/windows/observe-input.ps1` 与 `tools/input_observation.py`。观察入口固定调用同目录 `diagnose.ps1 -ReadDdc`，用户用显示器菜单手动切换画面；脚本不发送显示器写命令。A1 源码和原交付包保持原内容。

最终重新执行上述完整测试命令：**57 项，50 项通过，7 项 Windows 专属测试跳过，0 失败**，用时 36.216 秒。通过数量不包含跳过项。

- 观察报告契约与 CLI：10 项通过，覆盖时间顺序、嵌套 A1 契约、失败间隔、显示器消失、空样本与不推断主机映射。
- 观察脚本：12 项中 11 项通过、1 项 Windows 入口实机测试跳过。便携 PowerShell 7.6.6 的真实假工作进程验证了非 ASCII 报告、非零退出、非法报告、超时终止、双管道大输出、15 秒限时采样、路径与安全保存。
- 独立质量审查另外用过长目标文件名触发 `File.Move` 失败，确认临时文件已创建和写入后仍能正确清理，目录无残留。
- 审查发现并修复了 PowerShell 数组比较误判、非 ISO 日期误判、单元素根数组展开，以及假工作脚本在 Windows PowerShell 5.1 下的编码问题；对应回归验证通过。

以上便携测试结果不等于 Windows PowerShell 5.1 或真实硬件测试通过。新增 Windows 入口用例与原有 6 项 Windows 用例一起跳过。用户后来提供的单次回读和连续观察报告属于独立硬件证据，不能据此把这 7 项自动化测试改为通过。

观察包 `dist/switchdesk-input-observation.zip` 仅有 `observe-input.ps1`、现有 `diagnose.ps1`、现有 `Probe.cs`、`README-observation.zh-CN.md` 四个文件。ZIP CRC、文件清单、文件内容与审查后的源码一致，SHA-256 校验通过。未包含报告、便携运行器或本机信息。

SHA-256：`9065366142c80f62e16eb78788647dfb35134c729930760f7e0b47ab37798465`，另存于 `dist/switchdesk-input-observation.sha256`。用户操作步骤见 [观察说明](../native/windows/README-observation.zh-CN.md)。收到观察报告及实际看到的画面确认后，才能据此继续选择 A2b 的主动切源验证路线。

A2a 的两个代码任务已通过独立需求与质量审查，最终集成交付审查无阻断项。没有创建 Git 仓库、提交或推送。

## 用户返回的 Windows 连续观察报告

用户随后提供的原始报告保存为 `reports/windows-input-observation-2026-10-17T070835Z.json`，已通过 `validate_observation` 校验。开始于北京时间 2026-10-17 15:08:35.746（报告 UTC 时间 `2026-10-17T07:08:35.746Z`）。

| 项目 | 观测结果 |
| --- | --- |
| 请求 / 实际持续时间 | 45 秒 / 45.009 秒 |
| 采样数 | 18 |
| 工作进程成功 / 超时 / 错误 | 18 / 0 / 0 |
| 每次枚举的显示器 | 均为 1 台 |
| DDC 输入回读 | 18 次均成功，值始终为 17 |
| 警告 | 0 |
| 观察到的输入值变化 | 无 |

这份报告证明本次运行的 18 个采样点均可回读输入码。未采样的间隔不能视为持续可读证据；没有采集屏幕画面，也不能从“18 次成功”推断实际完成了 Windows → Mac → Windows 切换。用户随后明确说明“啥也没接，就是跑了个脚本”：没有搭建双机接屏和手动切换场景，也没有执行先前说明的切换步骤。因此将本报告归为当前连接的回读基线，不能用于确认 27G2 型号、非当前输入时的回读能力、主机输入映射或软件切源。报告中的通用显示器名称仍不能独立证明设备就是 27G2；不把这一澄清解释为硬件故障。无需重复同一基线采样。

## 仍需验证

27G2 的双向切源与切回路线、真实黑屏时间、两种键鼠来源的接管和释放、Finder 与 Explorer 文件/文件夹粘贴、非当前输入的桌面捕获，以及三种 Mac 视频接法的硬件矩阵均尚未通过验收。
