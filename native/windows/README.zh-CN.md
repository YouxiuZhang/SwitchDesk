# 双栖 SwitchDesk：Windows 诊断包

这是开发阶段的只读诊断工具，用来确认你的 Windows 与 AOC 27G2 的实际连接能力。它不是切屏软件，当前不会共享键鼠、剪贴板或画面。

## 运行

1. 让 Windows 正常显示在 27G2 上，保持现有视频线连接。
2. 完整解压此文件夹，保留 `Probe.cs` 与 `diagnose.ps1` 在同一目录。
3. 在该目录打开 PowerShell，执行：

```powershell
.\diagnose.ps1 -ReadDdc -OutputPath .\windows-ddc.json
```

该命令只查询显示器当前输入口，不发送切屏指令。运行完成后，目录中应出现 `windows-ddc.json`；可以将这个报告提供给开发者。脚本不会自动上传。

若只想枚举显示器，不查询输入口，可以执行：

```powershell
.\diagnose.ps1 -OutputPath .\windows-basic.json
```

已有报告不会被覆盖；重新采集时换一个文件名。去掉 `-OutputPath` 时，报告直接显示在终端。

## 环境与数据

目标环境为 Windows 10 22H2 / Windows 11 x64，使用系统自带 Windows PowerShell 5.1 和 .NET Framework 编译能力，不需要安装 .NET SDK，也无需管理员权限。当前包尚待 Windows 实机验证；Mac 上的源代码和编译检查不能代替此验证。

报告只包含系统版本、CPU 架构、显示器名称/编号/当前模式、可选输入口查询结果和错误说明。不读取屏幕画面、按键、剪贴板正文、机器名、IP 或设备序列号；不联网、不申请系统权限、不更改显示器设置。

如果提示执行策略禁止运行，保留错误信息并反馈。不要为此关闭系统防护或修改全局执行策略。

## 看结果

- `readable`：这次能读到输入口，不代表已经验证可以切过去或切回来。
- `unsupported`：本次接口明确返回不支持。
- `error`：查询失败，保留错误码继续排查；不能据此断定显示器永远不支持。
- `not_tested` / `not_checked`：尚未执行相应验证。

未枚举到显示器表示当前进程看不到它，不一定是没有连接。报告中的自动切屏、文件粘贴和非当前输入捕获，在此阶段始终为 `not_tested`。
