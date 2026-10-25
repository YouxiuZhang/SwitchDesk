# SwitchDesk Capability Probe Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 先交付 Mac 与 Windows 可运行的只读诊断程序，记录真实显示器、授权和 DDC 输入回读能力，为后续双向切屏验证提供依据。

**Architecture:** 这是设计阶段 A 的第一个独立子项目。两端以同一 JSON 结构报告事实；未知、不支持与已验证明确分开。默认诊断不切屏、不弹系统授权、不读取剪贴板正文或屏幕像素，不监听键盘、不启动网络监听。

**Tech Stack:** Mac 使用 Swift 与系统 Framework；Windows 使用 PowerShell 5.1 + 内嵌 C# / Win32；Python 标准库进行报告验证与行为测试。后续桌面产品仍按 Tauri 2 / Rust 架构推进。

---

## 范围与执行约定

用户已在 V0.4 原型后回复“继续”，视为通过当前设计进入实施。当前目录不是 Git 仓库，不能创建 worktree；直接在当前独立项目目录开发。不执行 git init、commit 或 push。当前会话可访问 Mac 和 27G2G4，尚无 Windows 执行环境。Mac 实测与 Windows 静态检查必须分别记录，不把后者写成 Windows 实测通过。

阶段 A 后续按独立计划继续：A2 双向切源与恢复；A3 输入接管与释放；A4 Finder/Explorer 原生延迟文件粘贴；A5 非当前输入桌面捕获。当前子项目不能证明这些能力已实现，也不改变整体验收范围。

## 文件结构

```text
native/macos/Probe.swift                 只读显示器与权限诊断
native/macos/build-probe.sh             编译命令与本地缓存目录
native/windows/Probe.cs                 Win32 显示器枚举、可选 DDC 回读
native/windows/diagnose.ps1             编译/调用 C#、写 JSON、友好错误
tools/probe_report.py                   同一报告验证与摘要
tests/test_probe_report.py              报告契约、未知与失败状态测试
tests/test_probe_cli.py                 CLI 可运行性与只读默认行为
tests/test_windows_probe.py             可选 PowerShell 语法、编译与平台保护检查
docs/diagnostics.md                     两端操作、字段定义与验证边界
reports/                               本地实测结果（忽略，不含屏幕内容）
dist/                                  可交付的 Mac 二进制和 Windows 脚本包（忽略）
```

## 报告契约

报告输出 UTF-8 JSON。`schema_version` 固定为 1；`platform` 为 `macos` 或 `windows`。不用设备序列号、用户名、机器名、IP 或剪贴板内容。每次采集时间使用 UTC ISO8601。

完整结构示例（仅说明格式，不作为真实结果）：

```json
{
  "schema_version": 1,
  "platform": "macos",
  "os_version": "26.5.1",
  "architecture": "arm64",
  "collected_at": "2026-10-17T12:00:00Z",
  "read_only": true,
  "displays": [
    {
      "id": "2",
      "name": "27G2G4",
      "builtin": false,
      "width": 1920,
      "height": 1080,
      "refresh_hz": 144,
      "ddc_input": {"status": "not_tested", "value": null, "detail": "未请求 DDC 回读"}
    }
  ],
  "permissions": {"screen_capture": "not_checked", "input_capture": "not_checked", "input_injection": "not_checked"},
  "capabilities": {"automatic_switch": "not_tested", "native_file_paste": "not_tested", "inactive_display_capture": "not_tested"},
  "warnings": []
}
```

`ddc_input.status` 允许 `not_tested / readable / unsupported / error`，只有 `readable` 可以携带有效 uint16 输入值；其他状态必须为 null。能回读不能推导能切屏或双向切回。`permissions` 允许 `granted / denied / not_checked / not_applicable`；只说明当前诊断进程权限，不推导将来安装的应用已获权。`capabilities` 本阶段全部 `not_tested`。`warnings` 使用文本数组，不输出未经筛选的原始系统信息。

## Task 1：报告契约与验证器

**Files:** Create `tools/probe_report.py`; Test `tests/test_probe_report.py`.

- [x] 先写测试：拒绝版本不符、平台不符、错误状态携带数值、负分辨率、布尔值充当整数、NaN 刷新率、无字段；允许显示器数组为空但摘要明确说明无法确认显示器；拒绝把 `automatic_switch` 标为通过。
- [x] 运行 `python3 -m unittest discover -s tests -p 'test_probe_report.py' -v`，确认因验证器尚不存在而失败，然后实施验证逻辑。
- [x] 实现纯函数接口及 CLI：

```python
def validate_report(report: object) -> list[str]:
    """Return precise path-addressed validation errors, no mutation or I/O."""

def summarize_report(report: dict) -> str:
    """Return Chinese diagnostic summary; readable DDC never means switch verified."""
```

CLI 为 `python3 tools/probe_report.py <report.json>`；非法 JSON / 契约错误返回 2；合法报告返回 0 并打印摘要。`--json` 输出经过验证的报告，不自动重写磁盘文件。不得因单个屏幕回读失败丢失其他屏幕信息。
- [x] 回归测试全部通过，再做需求审查与代码质量审查。保留测试结果，不提交。

Task 1 验证：18 项测试通过；独立需求审查与代码质量审查通过。异常 JSON 和 Unicode 的修复已复审。

## Task 2：macOS 原生诊断

**Files:** Create `native/macos/Probe.swift`, `native/macos/build-probe.sh`; Test `tests/test_probe_cli.py`.

- [x] 先写 CLI 测试：`--help` 返回 0；未知参数返回非零且 stderr 有说明；默认输出合法 JSON 且全部主动能力为 `not_tested`；`--output` 写文件时拒绝覆盖已存在文件；文件路径含空格可用。
- [x] 使用系统 SDK 编译。通过 CoreGraphics/NSScreen 获取当前显示器 id、名称、是否内置、物理像素及刷新率。只输出白名单字段，不读取或记录显示器序列号。
- [x] 权限使用不触发授权弹窗的 preflight 查询；不调用 request 权限函数，不捕获屏幕，不创建输入事件 tap。权限只能表示当前进程状态。
- [x] 内置屏幕 DDC 标记 `unsupported`；外接屏幕默认 `not_tested`。本阶段不通过连接器名称或已识别型号虚构 DDC 成功。可选外部 DDC 工具接入留到 A2；当前程序无需下载第三方依赖。
- [x] 采集失败时仍输出合法报告和 warnings，不能把权限不足/沙箱看不到显示器错误表述为“未连接显示器”。可见性不足的摘要用“当前进程未枚举到显示器”。
- [x] 编译脚本在项目 `.build/macos` 内放模块缓存和编译产物，最终二进制放 `dist/macos/switchdesk-probe`。不要求 Xcode GUI、不申请管理员权限。

验证命令：

```sh
bash native/macos/build-probe.sh
dist/macos/switchdesk-probe --help
python3 -m unittest discover -s tests -p 'test_probe_cli.py' -v
dist/macos/switchdesk-probe --output reports/macos-current.json
python3 tools/probe_report.py reports/macos-current.json
```

- [x] 在当前 Mac 上执行只读采集；需要时仅为系统显示器枚举申请沙箱外执行。记录 27G2G4 和内置屏幕的实际输出。独立完成需求审查与代码审查。

Task 2 验证：7 项 CLI 测试通过，全量 25 项通过；Mac 实机报告识别到 27G2G4 与内置屏。需求审查通过；写失败清理修复经质量复审通过。

## Task 3：Windows 原生诊断

**Files:** Create `native/windows/Probe.cs`, `native/windows/diagnose.ps1`, `tests/test_windows_probe.py`; add contract fixtures under `tests/fixtures/` if required.

- [x] Windows 使用系统 PowerShell 与 Add-Type，无需安装 .NET SDK。检测 Windows 平台；非 Windows 返回清楚错误，不冒充成功。
- [x] C# 枚举 `EnumDisplayMonitors` → `GetMonitorInfo` → `EnumDisplaySettings`。只输出名称、屏幕尺寸和刷新率；无物理屏幕或失败时进入 warnings。Windows 的内置判断没有可靠证据时输出 null，验证器允许 `builtin: null`。
- [x] 默认不读取 DDC。用户显式加 `-ReadDdc` 时才执行 `GetNumberOfPhysicalMonitorsFromHMONITOR` → `GetPhysicalMonitorsFromHMONITOR` → `GetVCPFeatureAndVCPFeatureReply(0x60)`，在 finally 内销毁成功取得的物理显示器句柄。
- [x] 对一个逻辑显示器映射多个物理显示器的情况，每个物理显示器分别输出结果；不能随意把第一个物理屏幕当作整个逻辑屏幕。DDC 枚举/回读失败保留已有枚举信息并报告原因。
- [x] 不包含 `SetVCPFeature`、剪贴板正文访问、键鼠钩子、网络、提权或执行策略修改。Windows 脚本支持 `-OutputPath`，使用 UTF-8 无 BOM JSON，默认输出 stdout。拒绝覆盖已有文件。
- [x] 采集时间、OS 版本、架构、权限与能力字段遵循同一契约；当前无法验证的权限为 `not_checked`。输出可被 `probe_report.py` 验证。

Windows 实机命令：

```powershell
.\diagnose.ps1 -OutputPath .\windows-current.json
.\diagnose.ps1 -ReadDdc -OutputPath .\windows-ddc.json
```

- [x] 当前 Mac 只做源代码/接口审查与契约 fixture 验证；若没有 Windows 或 PowerShell 运行器，明确记录“Windows 未实机运行”，不安装全局环境、不修改执行策略来凑通过。
- [x] 使用项目内便携 PowerShell（如可用）检查脚本语法、C# 5 编译、P/Invoke 结构体尺寸及非 Windows 平台保护；测试通过环境变量 `SWITCHDESK_PWSH` 指定运行器。没有运行器时明确跳过。此检查不替代 Windows PowerShell 5.1 和 Win32 硬件实测。
- [x] 两阶段审查通过后，将 `Probe.cs`、`diagnose.ps1` 和中文说明打包进 `dist/switchdesk-windows-probe.zip`，供用户在其 Windows 上运行。

Task 3 验证：本机 4 项便携检查通过，6 项 Windows 集成测试明确跳过；需求审查与代码质量审查通过。Windows 诊断包已生成并核对内容与 SHA-256。Windows 硬件和 PowerShell 5.1 仍待实测。

## Task 4：交付与验证记录

**Files:** Create `.gitignore`, `README.md`, `docs/diagnostics.md`, `docs/verification-2026-10-17.md`, `native/windows/README.zh-CN.md`; update design progress and this checklist.

- [x] `.gitignore` 包含 `.build/`、`dist/`、`reports/`、`.superpowers/`、`__pycache__/`，不忽略源文件或设计文档。
- [x] 文档写清各命令、需要开发工具与否、诊断数据范围、Windows 无法运行脚本时不要关闭系统防护、诊断不等于成品。Mac 报告本地保存；Windows 输出留在用户机器，仅用户主动提供时才交换。
- [x] 完整运行 `python3 -m unittest discover -s tests -v`，检查 Mac 二进制重新构建与一次真实报告验证。测试只验证有价值的契约和可观察行为，不为文案/目录复制添加形式测试。
- [x] 打包后核对 ZIP 内文件列表及 SHA-256；Mac 二进制用当前 Apple Silicon 构建，未签名/公证情况如实记录。
- [x] 自审并独立审查全部改动。记录已完成项与实际受阻项；不声称双向切屏、输入共享、文件粘贴或预览已通过。

Task 4 验证：最终 35 项测试中 29 项通过、6 项 Windows 集成用例跳过；两项产物 SHA-256 校验通过，集成交付审查无阻塞问题。A1 代码和诊断包已交付，Windows 实机结果仍待用户运行后提供。

## 范围检查

本计划完整覆盖阶段 A1 的诊断交付。A2–A5 仍须实现，不属于此次诊断包的完成声明。与用户界面约定一致：这些是开发验证工具，将来由产品后台调用；不会重新增加连接检测独立页面。
