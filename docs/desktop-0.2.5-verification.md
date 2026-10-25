# 双栖 0.2.5 验证记录

## 用户问题与证据

- 实际原生界面与已确认的 localhost 原型不同。已读取原型 DOM/CSS，保存 design/reference-prototype.html、design/prototype-reference.png，并记录 design/prototype-alignment.md。
- 用户再次报告 Mac 已连接、目标未知、“恢复 Windows 画面”灰色。只读检查确认本机实际运行 dist/product/双栖 2.app（0.2.4）；Windows 当前版本无法从旧协议确定。不能仅凭截图断言远端版本或唯一故障分支。
- 找到旧修复遗漏：recoveryMapping 只在视觉确认或恢复已确认配置时建立。安全试切前虽读到了 Windows 原输入，却没有保存独立恢复地址。未勾选确认的试切无法使用 Mac 恢复入口。

## 修改

- Mac / Windows 原生界面按原型改为三项侧栏、单张淡紫切换卡、监视器内真实预览、设备卡、来源卡、共享卡，增加可用的预览放大。配对、确认、权限、传输等仍走已有真实操作。
- 试切前通过新的只读结果验证显示器 ID/name、映射代次、Windows 原输入，再保存只允许返回 Windows 的快照。
- 试切时标记当前画面待确认并发布恢复能力。恢复请求通知正在执行的守护进程提前返回；不另起重叠写入。
- 试切结果以显示器映射代次关联，输入权限或网络状态变化不再使其失效。
- 区分只验证过 Windows 返回地址与已确认 Mac 画面的映射。恢复前者不会自动授予普通切换或键鼠转发权限。
- 远端状态提供 app_version 与 recovery_detail；Mac 设置显示双方版本，未知画面下恢复禁用时展示具体原因。
- 继续保留 worker 写入封锁、身份和输入检查。不猜测缺失的接口记录，不宣称不可读的显示器恢复成功。

## 自动化验证

- test_trial_recovery.py：先见到 3 个预期失败，修复后通过；另加入“活动试切应通知原守护进程”的用例，先失败后通过。共 5 项测试，覆盖 7 个情形。
- test_display_recovery.py：11 项通过。
- test_coordinator_switch.py：5 项通过。
- test_observed_confirmation.py：5 项通过。
- test_windows_product.py：2 项通过。
- 合计 28 项。试切新增测试覆盖真实 Coordinator 的前置验证、恢复调度与守护进程信号文件；原生 DDC 和实际子进程仍是测试边界，未宣称完整物理试切经过验证。

## 构建与界面

- Windows C#5 / .NET Framework 4.8 x64 全量交叉编译成功。布局对多档逻辑宽度进行了边界检查，未在 Windows 真机运行渲染。
- Mac arm64 / macOS 13 全量编译，临时签名严格验证成功。
- 使用独立 bundle ID com.switchdesk.desktop.ui-review 启动同一 Mac 界面，核验三页导航、设置版本。该模式不创建会话或键鼠/剪贴板服务，不读取配对密钥，不改变真实设备连接。
- 实际离线界面截图：dist/product/SwitchDesk-Mac-0.2.5-UI.png。
- ZIP 与 NSIS 均通过 7zz 完整性检查；从 NSIS 提取的 SwitchDesk.exe / README 与构建产物逐字节一致。校验和见 dist/product/SHA256SUMS-0.2.5.txt。

## 测试限制及升级

两端需退出旧进程并一起升级。旧版尚未记录的返回口无法凭空恢复；需在 Windows 重新选择显示器、Mac 接口并开始一次安全试切，建立返回地址。已确认并保存的匹配配置仍沿用恢复流程。

未执行 Windows 真机 UI、AOC 双机物理切换，也未验证一秒切换目标。硬件在非活动输入上若不能枚举或读取 DDC，软件仍可能无法回切。

交付 SwitchDesk-Mac-0.2.5.zip 和 SwitchDesk-Windows-0.2.5.exe。未提交或推送。
