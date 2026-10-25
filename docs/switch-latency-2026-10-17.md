# 切屏黑屏约 10 秒：本轮定位

用户将正常切换验收收紧为按键到画面与键鼠可用均 ≤ 1 秒，并确认此次操作为“安全试切并自动返回”或旧版“试切到 Mac”。随后用户确认：先黑屏，之后外接 AOC 显示器出现 Mac 桌面，再返回 Windows。因此已确认目标输入可显示 Mac；此前报告的约 10 秒不能直接作为单向黑屏的精确时长。正常单次切换从触发到目标画面/输入可用的时间尚未测得。

源码证据：`native/windows/product/Program.cs` 的 `Guardian` 在发出切至目标命令前启动秒表，至少等待至 10000 ms，再尝试恢复原输入；提前收到 restore 会结束等待。目标 worker 最多等待 10000 ms，恢复 worker 最多 20000 ms。这是首次校准的观察与恢复流程，计时不代表故意维持黑屏。

正常路径 `Coordinator.SwitchAsync` 不调用 Guardian，没有 10 秒固定等待。它启动独立 worker，`MonitorControl.Execute` 依次枚举选中显示器、读取原输入、发送一次 SetVCPFeature、读取确认。20 秒参数是驱动阻塞的最大超时，成功返回不会固定等待至超时。固定降低 timeout 只会提前报错，不能保证物理画面更早恢复。

当前不能将用户报告归因为某一个显示器时延，也不能宣称修改为 1 秒已完成。目标完全无信号的假设已被用户实际看到 Mac 桌面排除。正常路径未发现固定 10 秒等待；尚缺正常切换的实际黑屏与输入恢复时间，不能通过缩短校准倒计时伪造提速。未改安全恢复时长，未发布声称提速的新包。

用户明确授权后已执行 `codegraph init -i -y`：成功索引 43 个文件、1597 个节点、3492 条边；未进行 Git 提交或推送。

参考：Microsoft SetVCPFeature 文档约 50 ms 指 API 返回耗时，不是目标画面稳定时间：https://learn.microsoft.com/en-us/windows/win32/api/lowlevelmonitorconfigurationapi/nf-lowlevelmonitorconfigurationapi-setvcpfeature 。
