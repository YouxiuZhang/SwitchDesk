# 双栖 0.2.7：恢复后自动同步状态

## 证据与根因

用户已看到 AOC 恢复 Windows，但 Mac 仍显示“恢复 Windows 画面”。实际运行界面核对为 Mac 0.2.5 / Windows 0.2.5.0。正常切屏路径在即时回读失败后置 target=unknown，之后没有只读重核对，无法在接口稍后可读时自动更新。

## 修改

切源写入成功、仅回读失败且进程清理安全时，按 500/1000/2000ms 间隔进行最多三次只读核对（单次 worker 上限 3 秒）。读到目的接口后恢复已确认的映射、更新 target 并同步两端；下一次主按钮从 Windows 切向 Mac。核对不再写 DDC、不自动 armed，不依赖键鼠权限或 Mac 联网。以操作编号、映射版本和恢复快照拒绝旧结果；成功同步时撤销旧命令，避免过时的重复恢复继续执行。若仍读不到，保留未知状态及可用的显式画面确认，不宣称恢复成功。

## 验证

- 新回归先见到 4 项预期失败，修复后 5 项通过，涵盖恢复后延迟读成功、双方 state 与下一次目的地、只读不启用输入、缺权限/断线、旧值/失败的次数限制、显示器变更及进程封锁。
- 最终完整 product Python 回归 51 项通过（包括本机 TLS 互通及证书拒绝）；不发送实际切屏或键鼠输入。
- Windows C#5/net48 x64 和 Mac arm64/macOS13 优化构建通过；Mac 严格签名校验通过。
- ZIP / NSIS 完整性测试通过；Windows 安装载荷 EXE/README 与构建文件一致；Mac ZIP 版本 0.2.7/build9、可执行文件与构建一致。SHA256 见 dist/product/SHA256SUMS-0.2.7.txt。

## 当前运行状态

已退出用户当前运行的 dist/product/双栖 3.app，先备份到 .build/mac-before-0.2.7/双栖 3.app，再原路径替换为 0.2.7 并重新打开。连接已恢复，配对保留。实际设置页确认 Mac 0.2.7、Windows 0.2.5.0；Windows 尚需用户安装新包，当前不能宣称实机按钮已自动更新。辅助功能和输入监听仍显示未授权，未代用户授权。

版本证据：dist/product/SwitchDesk-0.2.7-Version-Check.png。

交付：dist/product/SwitchDesk-Windows-0.2.7.exe 与 dist/product/SwitchDesk-Mac-0.2.7.zip。未提交或推送。
