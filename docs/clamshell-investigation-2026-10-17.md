# 合盖切换排查（2026-10-17）

用户确认：接电后合盖可以在 AOC 上使用 Mac，但切换到 Windows 后不能再切回，Mac 熄屏。

## 已确认的系统证据

- `pmset -g log`：20:27:47 `Entering Sleep state due to 'Clamshell Sleep'`，`Using AC (Charge:96%)`。
- 20:27:57 由 `lid ... UserActivity` 唤醒。
- 20:29 供电检查为 AC、96%、charging。因此，接上电源不是完整解决方案。
- 当前原生程序未实现电源保活或合盖处理。正常输入依赖 Mac 上运行中的进程与网络连接。

切走显示器后外屏链路失活触发合盖睡眠，是与操作顺序相符的假设；尚无热插拔/EDID 记录，不能声称已证明 AOC 具体如何断开链路。

## 限时实验

20:31 左右启动系统自带 `/usr/bin/caffeinate -ds -t 300`，仅作五分钟实验，没有修改 pmset 持久设置。断言检查确认系统接受了 PreventSystemSleep 和 PreventUserIdleDisplaySleep，请用户按“Mac 合盖 → Windows → 等约 10 秒 → Mac”试验。

用户反馈：仍不能切回，开盖后才恢复。实验未解决问题。随后停止了本次 caffeinate 进程，核对 PreventSystemSleep、PreventUserIdleDisplaySleep 均恢复为 0，没有持久设置变更。

实验后的摘要记录了 20:33:38 `DarkWake to FullWake from Deep Idle`，没有在摘要中出现新的 Entering Sleep；因此不能把本次失败直接描述为与无断言时完全相同的深睡断网。可能保留了后台活动，但仍未恢复外屏输出；尚缺该段的应用连接与显示枚举证据。不能据此宣称修复或直接交付合盖功能。

公开文档与本机 SDK 对 PreventSystemSleep 的支持描述有差异，本机 SDK 将该旧常量标记为弃用。普通保活不是已验证的合盖切换支持；后续必须同时验证网络持续连接、显示器重新输出和输入恢复。

苹果外屏说明：https://support.apple.com/zh-cn/102501
苹果电源断言文档：https://developer.apple.com/documentation/iokit/kiopmassertiontypepreventsystemsleep

## Amphetamine 实测与 Power Protect 检查

用户已有 Amphetamine 5.3.2，手动开启无限期会话，并按截图取消“当显示器关闭时允许系统睡眠”。用户再次报告切到 Windows 后仍睡眠。

- 20:48:31 日志再次记录 `Entering Sleep state due to 'Clamshell Sleep'`，`Using AC (Charge:99%)`，20:48:36 开盖唤醒。
- 20:49 断言中确认 Amphetamine 的 Single-Use System / Display 均已运行约三分钟；这说明普通闲置保活生效，不说明合盖保护生效。
- IOPMrootDomain 报告 `SleepDisabled = No`；打开盖后 `AppleClamshellCausesSleep = No` 不能倒推合盖切源时的值。
- `/private/etc/sudoers.d/amphetamine_powerProtect` 不存在。作者说明 Power Protect 是 Apple Silicon 合盖支持在电源变化后的辅助方案；尚不能证明缺少它是本次全部原因。
- 从作者仓库下载 DMG 至 `.build/power-protect-review/`，仅解包和静态检查，没有安装或执行脚本。安装包在沙箱外只读复核：Developer ID Installer William Gustafson (U5SR49N3PT)，可信时间戳，Apple notarization trusted。最初沙箱内签名失败不能作为真实签名无效结论。
- 载荷配置只授权 `%admin` 免密执行 `/usr/bin/pmset -a disablesleep 1` 和 `/usr/bin/pmset -a disablesleep 0` 两个精确命令；该授权适用于管理员账户，不是仅 Amphetamine 单进程。辅助 AppleScript 供应用调用这些命令切换整机睡眠。
- 这会新增持久的管理员免密命令授权，必须向用户说明并获得安装确认，再进行管理员认证。当前未写入 sudoers 或系统电源设置，仍待确认与实测。

作者说明：https://github.com/x74353/Amphetamine-Power-Protect

## 用户授权后的安装与验证

用户已明确回复“允许”，同意上述两条精确 pmset 命令的管理员免密授权。2026-10-17 21:42，macOS 安装器显示“安装成功。软件已安装。”

- `/private/etc/sudoers.d/amphetamine_PowerProtect` 已存在，内容与审计的两条精确命令一致，没有扩大范围。
- 当前用户的 `~/Library/Application Scripts/com.if.Amphetamine/powerProtect.scpt` 已存在，SHA-256 与官方安装包载荷完全一致：`c616365813186116ddc73be8294a446f818464c13b2570525a15fd2162458300`。
- `pmset -g` 显示 `SleepDisabled 1`，IORegistry 显示 `SleepDisabled = Yes`。安装后的整机禁止睡眠设置已生效；不再仅有普通闲置保活断言。
- 已请用户执行“接电、Mac 画面、合盖、F12 切 Windows、等十秒、F12 切回 Mac”的实测，并同时确认键鼠。尚未收到结果，不能据配置生效直接宣布合盖切换修复。
