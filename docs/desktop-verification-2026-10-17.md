# 双栖 0.2.0 桌面联动版验证

验证平台：Apple M3 / macOS，Swift 6.3.2 以 Swift 5 模式、macOS 13 arm64 目标构建；Windows 使用 Roslyn C# 5 和 Microsoft .NET Framework 4.8 引用程序集交叉编译为 x64 GUI，NSIS 封装当前用户安装包。

## 已完成

- Mac 原生应用完整编译、`codesign --verify --strict` 通过；临时签名，未公证。实际打开控制台和设置，未授权录屏或输入，不以虚构画面模拟已连接。截图：`dist/product/mac-console.png`。
- Windows 产品全量 C# 5 编译成功，无警告；NSIS 安装包完整性检测通过，解包后 EXE、配置、说明与构建产物逐字节一致。没有 Windows 环境，未运行安装程序。
- 原有回归：63 项，56 通过，7 项 Windows 原生集成测试因平台跳过。
- Windows 产品纯规则：2/2 通过，覆盖路径边界和试切/显式确认门禁。
- Swift 协议测试：24 项路径样例、UTF-8 长度封包与往返、报文上限、布尔值拒绝通过。
- 真 TLS 回环集成：2/2 通过。Swift 客户端对临时本地 TLS 服务验证双通道 hello、证书指纹、token、分片报文、心跳；错误证书被拒绝。测试未读取真实桌面、剪贴板或键鼠。
- 真文件传输模块互联：1 MiB + 7 字节、中文目录、空文件/目录往返校验通过；错误 SHA-256 不发布文件；取消释放等待；拒绝符号链接源。使用测试专用缓存，不经过 Finder 剪贴板。
- 独立静态审核与修复：welcome 首帧顺序、紧急恢复 epoch 门禁、迟到 DDC/试切结果隔离、显示拓扑变化、隐藏/锁屏后迟到预览拦截。
- 交付 ZIP 内含完整 `.app`；包完整性和二进制架构检查通过。DMG 工具在沙箱返回“设备未配置”，提升权限请求自动审核超时，改为可直接解压安装的 ZIP，未削弱系统保护。

## 尚需双机实测

Windows 自签名证书创建、原生钩子与 SendInput、Windows 安装卸载、UAC/锁屏恢复、真实 Finder ↔ Explorer 延迟粘贴、切到非当前输入后的捕获、不同线材与转接器、持续延迟及稳定性。用户已确认旧 0.1.0 能切屏，该证据仅覆盖其实际测试的显示器与接线。

当前两端均含切屏、键鼠来源、文本/图片/文件共享和预览实现；本记录不将编译或模拟端测试表述为全功能实机验收。无自动开机启动，无发布者签名，无自动放行防火墙。文件粘贴有 75 秒等待上限及 16 GiB / 4096 项清单上限。

## 复现

```sh
native/macos/build-app.sh
POWERSHELL_TELEMETRY_OPTOUT=1 .build/tools/powershell-7.6.6/pwsh -NoProfile -File tools/build-desktop-windows.ps1
SWITCHDESK_PWSH="$PWD/.build/tools/powershell-7.6.6/pwsh" python3 -m unittest discover -s tests -v
python3 -m unittest discover -s tests/product -p test_windows_product.py -v
python3 -m unittest discover -s tests/product -p test_tls_session.py -v
```

TLS 测试依赖先编译 `tests/product/session_driver.swift` 为 `.build/product-checks/session-driver`，并需允许本地回环监听。路径/文件测试位于 `tests/product/protocol_tests.swift`、`file_transfer_tests.swift`，须与对应产品源码编译；文件测试运行目录必须为新建专用空目录。
