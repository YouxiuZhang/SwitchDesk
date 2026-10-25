# SwitchDesk 双端联动 Implementation Plan

> **For agentic workers:** 使用 dispatching-parallel-agents：在固定协议契约后，Windows实现、Mac网络/UI、Mac系统适配器具有独立文件所有权，可并行推进。根任务负责契约、构建、集成和验收。无需再次设计审批；用户明确要求完整双端版本。不做Git提交。

**Goal:** 交付可安装的Mac与Windows联动应用，完整实现已批准的切屏、可选来源键鼠、文字/图片/文件文件夹剪贴板、另一台实时预览。

**Architecture:** Windows保留用户已验证可工作的DDC作为切源执行端，两端TLS局域网会话。两个独立连接分离输入与媒体。Mac原生Swift/AppKit，Windows沿用C#5/net48/WinForms，以已确认交互风格实现实际系统功能。

**Tech Stack:** Apple Network/Security/ScreenCaptureKit/AppKit/CGEvent，Windows SslStream/DPAPI/Win32 Hooks+SendInput/OLE Clipboard/GDI；NSIS及macOS .app/.dmg。

---

## Task 1: 固定契约和已有证据

Files: `docs/protocol/desktop-v1.md`, `native/macos/app/Protocol.swift`。

- [x] 记录用户已在Windows 0.1.0成功切换；不能把此结果扩展到未测线路或共享功能。
- [x] 明确长度封包、认证、双通道、消息字段、键码、状态epoch、文件清单/分块/校验、Mac跨文件接口。
- [x] 编写并测试无效路径/报文上限/输入状态等纯规则；禁止未配对注入。

## Task 2: Windows产品端

Owned files: `native/windows/product/*.cs`, `tests/product/test_windows_product.py`。复用只读`Probe.cs`与DDC`MonitorControl.cs`，0.1.0源文件不改。

- [x] TLS服务与证书/token的DPAPI存储、一次明确配对授权、撤销/重连/双通道。
- [x] 保留隔离DDC首次确认/普通切源/恢复；协调epoch/source/target和能力门禁。
- [x] 全局键鼠捕获/抑制与SendInput、紧急恢复、退出及租约释放。
- [x] 文本/PNG/OLE延迟文件剪贴板、仅已复制根目录的流式传输/校验/取消。
- [x] 真桌面预览、可见性与锁屏停止；中文控制台、托盘、设置与配对提示。
- [x] 用现有Roslyn交叉编译，核心规则测试，修复集成问题。

## Task 3: Mac网络与界面

Owned files: `native/macos/app/Session.swift`, `native/macos/app/Main.swift`。

- [x] 按固定契约实现Network TLS客户端、精确pin、Keychain邀请、控制/媒体两连接与心跳断线。
- [x] AppKit控制台、配对输入/邀请文件、输入来源、开关、预览与过期遮盖、权限提示、菜单栏。
- [x] 严格按NativeCoordinator接口接线；收发异步避免文件provider与主线程死锁。
- [x] 持久配置但不在重连自动恢复远控；退出与撤销断开并释放。

## Task 4: Mac系统能力

Owned files: `native/macos/app/NativeCoordinator.swift`, `InputBridge.swift`, `ClipboardBridge.swift`, `FileTransfer.swift`, `PreviewBridge.swift`。

- [x] CGEvent tap+post、VK映射、热键、仅远控时抑制、注入事件防回环、权限与租约。
- [x] NSPasteboard文字/图片去回环，fileURL延迟provider与流式文件传输；main外处理网络数据。
- [x] ScreenCaptureKit有限尺寸/帧率预览，仅已配对+订阅+授权时工作，锁屏暂停。
- [x] NativeCoordinator实现固定接口，纯路径/帧/协议规则可脱离权限测试。

## Task 5: 集成、安装与交付

Owned by root: `tools/build-desktop-windows.ps1`, `native/windows/installer/SwitchDeskDesktop.nsi`, `native/macos/build-app.sh`, app Info.plist/entitlements, `tests/product/*`非agentowned测试, docs。

- [x] Windows net48 x64构建及当前用户NSIS安装；Mac arm64 macOS13 .app、临时签名与ZIP；DMG封装权限审核超时，ZIP直接交付。
- [x] 协议互操作测试、状态/路径/文件校验及完整旧测试回归；实际本机能跑的Mac启动检查。
- [x] 独立需求与关键质量审查后修复；不重复扩大无关检测工具。
- [x] 核对PE/Mach-O架构、包内容、签名状态与SHA；保留0.1.0原安装包。
- [x] 交付两包、最短配对步骤、必须由用户授予的系统权限；明确实机验证覆盖范围，不冒称所有原生能力已实测。

验收边界：上述完成项指源码、构建、本文列明的自动化和本机检查；双机原生实测仍待用户执行，详见 docs/desktop-verification-2026-10-17.md。
