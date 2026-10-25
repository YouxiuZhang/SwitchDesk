# Window Handoff Implementation Plan

**Goal:** 保留双屏扩展桌面，AOC 切 Windows 时自动把普通 Mac 窗口收回笔记本。

**Architecture:** 纯布局策略负责目标矩形与恢复条件；WindowHandoff 负责可取消的状态机、屏幕快照与 AX 消息；NativeCoordinator/Main 连接状态和设置。窗口迁移串行执行，输入与媒体队列保持独立。

**Tech Stack:** Swift 5、AppKit、ApplicationServices、Python unittest。

- [x] 新建 `tests/product/window_handoff_tests.swift` 和 Python 运行入口，先验证缺失功能失败。场景使用 1920×1080 外屏主屏、位于下方或左侧的 1512×982 内屏；正常窗口、超大窗口、内屏窗口、手动移动、重复轮询、屏幕离线和多外屏分别断言。
- [x] 新建 `native/macos/app/WindowHandoffPolicy.swift`：屏幕坐标与可见区域、单一外屏选择、窗口归属、映射与边界、恢复判定和目标状态代数。
- [x] 新建 `native/macos/app/WindowHandoff.swift`：主线程获取屏幕快照，utility 串行队列枚举普通应用窗口；AX 消息短超时、跳过全屏/最小化、只记录已成功移动的窗口。Windows 目标每秒扫描新窗口；Mac 恢复只等待原外屏 15 秒；每个窗口修改前检查代数。
- [x] 在 `NativeCoordinator.swift` 接入连接、目标、停止和启用设置；`Main.swift` 添加设置与状态，不影响 UI review 隔离。更新 UI review 假协调器接口。
- [x] 执行新测试和受影响的原有测试、构建 Mac，修复编译或行为问题。仅在新代码通过后运行完整产品回归。
- [x] Mac 版本更新 0.2.11/build12，打包至隔离目录；补充使用说明、校验值和验证记录。当前运行的 0.2.10 不直接替换，不自动提交推送。

验证命令：

```sh
python3 -m unittest discover -s tests/product -p 'test_window_handoff.py' -v
python3 -m unittest discover -s tests/product -p 'test_*.py' -q
SWITCHDESK_APP_OUTPUT="$PWD/dist/product/mac-0.2.11/双栖.app" sh native/macos/build-app.sh
codesign --verify --strict --verbose=2 dist/product/mac-0.2.11/双栖.app
```
