# 双栖 SwitchDesk

Mac 与 Windows 共用显示器、键鼠和剪贴板的桌面软件项目。

当前源码版本 **Mac + Windows 0.2.10 桌面联动版**：更新为浅银双窗口图标，Windows 安装时默认创建桌面快捷方式。保留 TLS 配对、显示器切源、可选键鼠来源、文字/图片/文件剪贴板和另一台桌面预览。Mac 为本地临时签名，Windows 尚无发布者签名。

[安装与配对步骤](native/macos/README.zh-CN.md) · [Windows 使用说明](native/windows/desktop-assets/README.zh-CN.md) · [本版验证记录](docs/desktop-0.2.10-verification.md)

此仓库包含两端源码、图标、设计文档和测试；安装包、第三方构建工具、本机报告及配对信息不入库。文档中提到的 `dist/`、`.build/`、`reports/` 均为本地产物目录。

Mac 包适用于 Apple 芯片、macOS 13+；Windows 包适用于 x64 Windows 10/11。两端均更新到 0.2.10，Windows 包含 0.2.9 的键鼠保持与移动合并修复。当前已授权的 Mac 运行实例未覆盖，安装新版后新图标生效。显示器检测在后台完成，首次映射与配对在设置中进行。

历史 Windows 0.1.0 切屏工具：[旧版使用说明](native/windows/app/README.zh-CN.md) · [旧版构建验证记录](docs/windows-app-verification-2026-10-17.md)

安装后打开双栖，两台电脑各用视频线连接同一显示器，选择显示器及 Mac 所接的 HDMI/DP 接口，然后点“试切到 Mac”。无需运行脚本。

此前已实现 A1 双端只读诊断，Mac 已实机运行；Windows 报告确认当前连接的输入源回读值为17。45秒观察中18次读取成功，但用户当时仅运行脚本，没有进行双机切屏，因此这些记录仅是回读基线，不能证明双向切源通过。诊断功能保留在后台，不增加独立检测页面。

诊断工具源码分别位于 `native/windows/` 和 `native/macos/`，说明见 [诊断文档](docs/diagnostics.md)。

历史诊断工具：[Windows 输入观察步骤](native/windows/README-observation.zh-CN.md)。这些工具不发送切屏命令；当前测试以安装版为入口。

0.2.2 修复显示器切换被键鼠权限和联网条件锁住的回归；输入注入仍单独检查配对、权限与显式启用。0.2.3 允许用户直接确认已看到 Mac 桌面并返回 Windows，核对当前输入后保存，不再要求自动试切结果成功。1 秒体验尚未实机验收。

## 文档

- [产品与技术设计](docs/superpowers/specs/2026-10-17-switchdesk-design.md)
- [第一项实施计划：双端诊断](docs/superpowers/plans/2026-10-17-switchdesk-capability-probe.md)
- [下一项实施计划：切屏期间的只读观察](docs/superpowers/plans/2026-10-17-switchdesk-input-observation.md)
- [诊断工具说明](docs/diagnostics.md)
- [Windows 安装测试版实施计划](docs/superpowers/plans/2026-10-17-switchdesk-windows-test-app.md)
- [实机与自动化验证记录](docs/verification-2026-10-17.md)

## 本地开发

### 当前桌面应用

Mac 需要 Apple Silicon、macOS 13+ 与 Xcode Command Line Tools：

```sh
sh native/macos/build-app.sh
```

输出为 `dist/product/mac/双栖.app`，构建脚本执行本地临时签名。

Windows 使用带 Roslyn 程序集的 PowerShell 7 与 .NET Framework 4.8 引用程序集。引用程序集需自行准备，使用 `-ReferenceDirectory` 指向包含 `mscorlib.dll` 等 DLL 的目录：

```powershell
pwsh -NoProfile -File tools/build-desktop-windows.ps1 -ReferenceDirectory /path/to/net48/reference
```

程序输出到 `dist/product/windows/`。安装器由 NSIS 3 构建：

```sh
makensis "-DAPP_DIR=$PWD/dist/product/windows" \
  "-DOUTPUT_FILE=$PWD/dist/product/SwitchDesk-Windows-0.2.10.exe" \
  native/windows/installer/SwitchDeskDesktop.nsi
```

现有桌面回归测试入口：`python3 -m unittest discover -s tests/product -p 'test_*.py'`。部分测试还依赖 Swift、PowerShell 或本地网络监听权限。

### 历史诊断与切屏工具

Mac 原生诊断使用 Apple Command Line Tools 中的 Swift；验证器只需要 Python 3。Windows 诊断使用系统自带的 Windows PowerShell 5.1，不要求 .NET SDK。

```sh
bash native/macos/build-probe.sh
python3 -m unittest discover -s tests -v
```

Windows 源码检查可以使用已有的 PowerShell，通过 `SWITCHDESK_PWSH` 指定路径；未找到运行器时会明确跳过相关检查。当前 Mac 验证使用项目内便携运行时：

```sh
SWITCHDESK_PWSH="$PWD/.build/tools/powershell-7.6.6/pwsh" python3 -m unittest discover -s tests -v
```

此命令仍会跳过 Windows 专属集成测试；跨平台编译与假工作进程测试通过不代表 Windows 硬件通过。

只读诊断不切屏、不采集画面、不读取剪贴板、不发送网络数据，也不主动申请系统权限。Windows 的 DDC 输入值回读需要明确使用 `-ReadDdc`。

Windows 安装版构建使用 `.build/tools` 内的便携 PowerShell/Roslyn、微软 .NET Framework 4.8 引用程序集和 NSIS。它直接输出 Windows PE，无需在本机安装 .NET SDK：

```sh
POWERSHELL_TELEMETRY_OPTOUT=1 .build/tools/powershell-7.6.6/pwsh -NoLogo -NoProfile -NonInteractive -File tools/build-windows-app.ps1
NSISDIR="$PWD/.build/tools/nsis/tool" .build/tools/nsis/tool/mac/makensis -V3 \
  "-DAPP_DIR=$PWD/dist/windows-app" \
  "-DOUTPUT_FILE=$PWD/dist/installer/SwitchDesk-Setup-0.1.0.exe" \
  native/windows/installer/SwitchDesk.nsi
```

这些能力未通过真机验证前，不报告兼容完成。

0.2.4 保留已确认的 Windows 恢复接口，切源回读失败或显示器变化通知不会锁死回切；权限/网络变化只释放输入，不丢弃物理切源结果。详见 [恢复验证](docs/desktop-0.2.4-verification.md)。

0.2.5 界面以当前原型为依据，详见 [对齐标准](design/prototype-alignment.md)。

0.2.6 增加有限回读重试、近期切屏的显式画面确认、分项键鼠权限诊断和切屏阶段耗时；黑屏 1 秒目标仍待实测。详见 [本版验证](docs/desktop-0.2.6-verification.md)。

0.2.7 在切屏后即时回读失败时后台核对实际接口；确认已恢复 Windows 后自动同步按钮为“切换到 Mac”，不额外写入或自动启用键鼠。

0.2.8 将全局切换统一为 F12，保留 Ctrl + Alt/Option + Esc 紧急恢复；修复 Windows 自绘按钮的残影、圆角边缘与滚动页宽度。Mac 若顶排默认调音量请按 Fn + F12，并授予辅助功能和输入监听权限。

Windows 0.2.9 修复正常切屏后的延迟回读清掉已授权键鼠状态的问题；同一次操作确认成功后继续使用原来源，断线、权限失效和紧急恢复仍会停用。鼠标移动排队时合并位移，不再直接丢弃移动，并减少每帧发送的写入次数；真实流畅度待双机验证。Mac 无需更新。
