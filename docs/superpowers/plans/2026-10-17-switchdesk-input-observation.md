# SwitchDesk Input Observation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 交付限时只读观察工具，记录用户手动切换显示器时 Windows 端的输入回读结果，为 A2 的实际切源命令确定映射与控制路径。

**Architecture:** 这是 A2 的前置实验 A2a。复用已在用户 Windows 上成功运行的 `diagnose.ps1 -ReadDdc`，每次采样用独立子进程和超时。工具自身不切屏；观测与软件主动切源分别验收。

**Tech Stack:** Windows PowerShell 5.1 / .NET Framework 进程 API；Python 标准库校验与摘要；项目内便携 PowerShell 用于控制逻辑测试。

---

## 已有证据与边界

用户于本轮提供 Windows 报告，已保存为 `reports/windows-2026-10-17-ddc.json` 并通过现有校验器：Windows x64、版本 10.0.26200.9457，一个物理屏、1920×1080 @60Hz、VCP 0x60 回读成功且值为 17，warnings 为空。报告显示的通用名称不能独立证明显示器型号；结合用户描述是其 27G2 测试环境。没有证据证明 17 一定对应 Windows、另一个值一定对应 Mac，不能仅凭常见输入码猜测映射。

当前不发送显示器写命令、不修改刷新率、不开网络、不读取画面/剪贴板/按键、不申请权限。用户手动切换显示器，应用后台不擅自切屏。没有新的产品检测页面。

当前目录不是 Git 仓库，沿用现有工作区；不 git init、commit、push。用户已授权继续原生开发，无需再次确认实现方式。

## 文件与接口

| 文件 | 职责 |
| --- | --- |
| `tools/input_observation.py` | 校验观察报告，按显示器摘要成功回读值与失败次数；复用 A1 验证器 |
| `tests/test_input_observation.py` | 嵌套报告、时间、失败样本、摘要不夸大能力 |
| `native/windows/observe-input.ps1` | 用户入口、受控子进程采样、限时观察、原子文件输出 |
| `tests/test_input_observation_cli.py` | 便携 PowerShell 假工作进程测试真实超时/错误/文件行为，Windows 入口集成检查 |
| `native/windows/README-observation.zh-CN.md` | 手动切换步骤与回读结果边界 |
| `docs/verification-2026-10-17.md` | 追加用户提供的 Windows 实测证据及本阶段验证记录 |

不修改 A1 的 `Probe.cs`、`diagnose.ps1`、报告契约或原诊断包，观察包单独包含其所需的现有文件。

## 观察报告契约

UTF-8 无 BOM。固定字段如下，拒绝额外字段，所有时间长度单位为整数毫秒，布尔值不可充当整数。

```json
{
  "schema_version": 1,
  "kind": "input_observation",
  "platform": "windows",
  "read_only": true,
  "started_at": "2026-10-17T07:00:00Z",
  "requested_duration_seconds": 45,
  "elapsed_ms": 45000,
  "samples": [
    {
      "started_ms": 0,
      "finished_ms": 300,
      "outcome": "ok",
      "exit_code": 0,
      "report": {}
    }
  ]
}
```

上例 `report` 的位置必须是完整 A1 报告，由现有 `validate_report` 校验且 platform 必须 windows。不是允许空对象。`samples` 允许 0–256 条，空样本必须明确摘要为“未取得观测样本”。`requested_duration_seconds` 为 15–120；`elapsed_ms` 非负。每样本 `0 <= started_ms <= finished_ms <= elapsed_ms`，后一个样本起点不早于前一个终点。

`outcome` 为 `ok / timeout / worker_error / invalid_report / cleanup_failed`。仅 `ok` 带完整合法报告及 exit_code 0。其他情况 report 为 null；timeout/cleanup_failed 的 exit_code 为 null；worker_error 的 exit_code 为非零整数；invalid_report 的 exit_code 为 0。不得把原始 stderr 或无法校验的输出写入报告。

## Task 1：观察报告校验和摘要

- [x] 创建 `tests/test_input_observation.py`。以 A1 合法报告 factory 构造可读 17、可读 18、可读 17 的样本，并覆盖中间 timeout、DDC error、显示器消失、多个显示器、零样本。必须确认摘要不出现“已验证切屏”或擅自将输入值命名为主机。

```python
def test_readable_sequence_is_not_a_switch_success(self):
    report = observation_with_inputs([17, 18, 17])
    self.assertEqual(validate_observation(report), [])
    summary = summarize_observation(report)
    self.assertIn("17", summary)
    self.assertIn("18", summary)
    self.assertIn("尚未验证", summary)
```

- [x] 运行 `python3 -m unittest discover -s tests -p 'test_input_observation.py' -v`，确认实现不存在时失败。
- [x] 实现下面固定接口，复用 A1 的 `validate_report`，正常模块导入与直接 CLI 两种方式均能使用。验证和摘要不修改输入、不写文件。

```python
def validate_observation(report: object) -> list[str]:
    """Validate the exact observation envelope and every successful A1 report."""

def summarize_observation(report: dict) -> str:
    """Describe observed input values and read failures without assuming host mapping."""

def main(argv: list[str] | None = None) -> int:
    """Read JSON, validate, print Chinese summary; valid 0, invalid 2."""
```

- [x] CLI 为 `python3 tools/input_observation.py <file.json>`。错误输出包含字段路径，非法 JSON/Unicode/过深解析返回 2，无 traceback；不覆写源文件。测试实际 CLI 与含空格路径，复用上一阶段的异常输入处理经验。
- [x] 测试通过后做独立需求审查和质量审查。

## Task 2：限时采样入口

- [x] 创建 `tests/test_input_observation_cli.py`，使用 `SWITCHDESK_PWSH` 或已安装 PowerShell。便携测试用临时 `.ps1` 假工作进程，真实测试子进程成功、非零退出、非法 JSON、超时终止、路径含空格和不覆盖文件。没有运行器明确 skip，不安装全局环境。
- [x] `observe-input.ps1` 参数为 `-OutputPath`（默认 `./input-observation.json`）、`-DurationSeconds`（默认45，15–120）、`-IntervalSeconds`（默认2，1–5）、`-ReadTimeoutSeconds`（默认4，1–10）。不提供用户可指定任意工作脚本的 CLI 参数。
- [x] 脚本可点源以加载函数，点源不采样、不写文件；正常入口先拒绝非 Windows。函数边界如下，允许给内部采样函数传假工作脚本做控制逻辑测试。

```powershell
function Invoke-SwitchDeskProbeSample {
    param([string]$Executable, [string]$ProbePath, [int]$TimeoutMilliseconds)
    # 返回 outcome / exit_code / report；主循环负责 monotonic timestamps。
}

function Save-SwitchDeskObservation {
    param([object]$Report, [string]$OutputPath)
    # PS SessionState 路径解析 + FileSystem 限制 + 同目录临时文件 + File.Move 独占发布。
}
```

- [x] 正常入口始终调用相邻 `diagnose.ps1 -ReadDdc`，使用当前 PowerShell 可执行文件、`-NoLogo -NoProfile -NonInteractive -File`；不使用 ExecutionPolicy 覆盖、EncodedCommand 或全局环境修改。按 Windows 参数规则处理空格路径。异步读取 stdout/stderr，避免管道填满后死锁；不将原始 stderr 保存为报告。
- [x] 用 Stopwatch 保持单调时间，单次等待不超过 ReadTimeoutSeconds 和剩余观察时间的较小值。超时终止该次子进程并有限等待确认；若无法确认终止，记 cleanup_failed 并停止后续采样。正常退出则解析完整 JSON。解析失败/明显违反 A1 结构或未测试能力约束记 invalid_report，不能记成功。Windows API 长时间阻塞不会让父进程无限等待。
- [x] 默认观察约45秒；退出时记录实际 elapsed_ms。入口开始前验证输出父目录存在和目标不存在；最终仍用原子防覆盖防竞态。子进程在 finally 清理。main 不读取键盘确认，不绑定热键。
- [x] 控制台说明让用户先保留 Windows 画面约10秒，用显示器菜单切到 Mac 保持约10秒，再切回 Windows等待结束。只记录实际回读，不把预期动作当作已发生。采样间隔内不忙循环。
- [x] 新增 Windows-only 入口测试，Mac 明确 skip；便携测试覆盖函数和写入行为，生成的观察报告通过 Task1 验证。完成双阶段独立审查。

## Task 3：交付与证据

- [x] 更新 A1 状态：收到用户 Windows 实测报告，显示器枚举与输入 17 回读有一次用户提供的成功证据；不能将全部6项 Windows 集成用例改为通过，亦不推断 PowerShell 版本。
- [x] 新建中文观察说明，强调只在开发验证中运行，日常产品保持后台检测。不提供猜测的输入口切换命令。
- [x] 生成 `dist/switchdesk-input-observation.zip`，包含 `observe-input.ps1`、现有 `diagnose.ps1` / `Probe.cs`、独立中文说明。核对 CRC、源码内容和 SHA-256；不包含本机报告、第三方运行器或其他文件。
- [x] 运行完整测试一次，区分便携控制逻辑、Mac 实测、用户提供的 Windows 结果、未运行的 Windows 用例。独立完成集成交付审查。
- [x] 交付可执行的观察包及用户操作步骤。

**后续依赖：** 已收到并校验 Windows 观察报告（45.009 秒，18 次读取均为 17，无超时或错误）。用户已确认仅运行脚本，没有搭建双机接屏和手动切换场景；本次仅是当前连接的回读基线。需先准备两台电脑分别接同一显示器的实际环境，再验证另一输入期间的回读和 A2b 软件切源；不要求为此重复运行相同基线测试。A2a 工具交付不代表双向切源已完成。

**最终验证：** 57 项测试中 50 项通过、7 项 Windows 专属跳过、0 失败；需求、代码质量及集成交付审查均通过。
