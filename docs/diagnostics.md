# 双端只读诊断工具

这些工具用于开发期间确认真实硬件条件。日常产品会在后台完成检测，用户无需进入检测页面。

## 会读取什么

白名单内容：操作系统版本、CPU 架构、当前进程可枚举的显示器名称/编号/尺寸/刷新率、诊断进程的权限预检状态。Windows 在显式加 `-ReadDdc` 后，另外读取显示器当前输入源的 VCP 0x60 值。

不读取屏幕像素、剪贴板正文、按键内容、设备序列号、用户名、机器名或 IP；不联网、不切屏、不申请系统授权、不修改执行策略。诊断报告只保存在用户指定的本地路径。

## macOS

开发者构建需要 Apple Command Line Tools。命令会把缓存写入项目 `.build/macos`，二进制写入 `dist/macos/switchdesk-probe`。使用构建好的二进制时无需再安装 Swift 开发环境。

```sh
bash native/macos/build-probe.sh
mkdir -p reports
dist/macos/switchdesk-probe --output reports/macos-current.json
python3 tools/probe_report.py reports/macos-current.json
```

也可直接运行 `dist/macos/switchdesk-probe`，JSON 输出到标准输出。`--help` 显示参数说明；`--output` 不覆盖已有文件，重复采集请换一个文件名。

沙箱或运行环境可能限制枚举。如果输出为空，含义是“当前进程未枚举到显示器”，不代表用户没有连接显示器。权限结果仅属于当前诊断进程；将来的安装版应用必须自行获得所需权限。诊断工具不会弹授权窗口。

Mac 写入时先生成同目录临时文件，完整写好后独占发布，避免留下半截报告。当前验证覆盖本机文件系统；外部或网络文件系统若不支持独占重命名会报错，此时可改为保存到本机目录。

当前本地构建面向 Apple Silicon。此开发二进制尚未进行发布签名或公证；遇到系统安全提示时保留提示，不使用关闭系统保护的命令。正式安装包的签名与分发在安装验收阶段完成。

## Windows

将诊断包完整解压到同一文件夹，保留 `Probe.cs` 与 `diagnose.ps1` 的相对位置。在该目录打开 Windows PowerShell，执行：

```powershell
.\diagnose.ps1 -OutputPath .\windows-current.json
```

需要验证显示器是否能响应输入源查询时，显式执行只读回读：

```powershell
.\diagnose.ps1 -ReadDdc -OutputPath .\windows-ddc.json
```

`-OutputPath` 不覆盖已有文件。去掉该参数时直接输出 JSON。脚本使用 Windows 自带编译能力，无需安装 .NET SDK，也无需管理员权限。默认诊断不进行 DDC 通信。

如果组织策略禁止运行脚本，记录错误，交由后续签名可执行程序解决；不要修改全局执行策略、关闭安全防护或粘贴绕过命令。Windows 诊断必须在 Windows 上实际运行；Mac 上的源代码审查不等于 Windows 验证通过。

## 如何理解结果

| 结果 | 可以证明什么 |
| --- | --- |
| 显示器被枚举 | 当前诊断进程能识别它 |
| `ddc_input.status = readable` | 此时此链路能回读输入值；不能证明可以切换、切回或满足 3 秒目标 |
| `ddc_input.status = unsupported` | 当前适配器明确不适用，或设备返回不支持 |
| `ddc_input.status = error` | 查询失败；需要结合错误码排查，不能直接判定硬件永久不支持 |
| `not_tested` / `not_checked` | 未执行相应验证，不能作为通过结果 |

输入代码按显示器实际回读记录，不能单靠常见编号推断 Mac/Windows 各自接在哪个口。校准时需要建立主机与输入口映射，并分别验证切过去和切回来。

报告契约与检查命令：

```sh
python3 tools/probe_report.py path/to/report.json
python3 tools/probe_report.py path/to/report.json --json
```

合法报告返回 0；JSON 或契约错误返回 2。报告中自动切源、原生文件粘贴、非当前输入捕获在本阶段均为 `not_tested`。

## 手动切屏期间的连续观察

Windows 已有一次输入码 `17` 的成功回读。下一步使用独立观察包，在用户用显示器菜单切换 Windows → Mac → Windows 时，记录 Windows 端是否仍能读取以及读到哪些输入值。

完整解压观察包后，在该文件夹运行：

```powershell
.\observe-input.ps1 -OutputPath .\input-observation.json
```

默认观察约 45 秒。先保留 Windows 画面约 10 秒，手动切到 Mac 并确认桌面约 10 秒，再切回 Windows 等待结束。具体步骤见 [观察工具说明](../native/windows/README-observation.zh-CN.md)。工具不会替用户切屏；日常产品仍在后台检测，不增加页面。

每次读取有独立超时。报告区分工作进程失败、超时、无效报告、无法确认终止，以及成功查询中的显示器消失或 DDC 不可读。不能把两次成功读数之间的失败略去后推断整段可用。

在开发机验证并查看按时间排列的摘要：

```sh
python3 tools/input_observation.py path/to/input-observation.json
```

提供报告时也需说明中间实际看到的画面。即使获得 `17 → 18 → 17`，也只是观察到输入值变化，还未证明软件可主动切源。

## 后续验证

在两端诊断结果具备后，准备带超时恢复的双向切源验证。输入共享、文件/文件夹粘贴与另一台画面的捕获分别有独立实验，不能从显示器检测成功推导。主动切屏测试由用户发起；后台健康检查不会突然切走用户正在使用的屏幕。

实际通过的检查及未覆盖部分见 [验证记录](verification-2026-10-17.md)。
