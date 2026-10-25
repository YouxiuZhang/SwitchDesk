# SwitchDesk Icon Implementation Plan

> 本会话内按顺序执行，遵守用户不提交、不推送的约定。

**Goal:** 发布浅银图标，并修复 Windows 桌面入口缺失。

**Architecture:** 一份生成的 PNG 源图，通过 Swift/AppKit 输出两端格式。Windows 由 Roslyn 嵌入 ICO，并为安装器、窗口和托盘绑定；NSIS 主安装段创建桌面快捷方式。Mac 使用 ICNS、PNG 及单色菜单栏符号。

**Tech Stack:** imagegen、Swift/AppKit、C# WinForms、Roslyn、NSIS。

- [x] 将浅银原图保存到 `design/brand/switchdesk-silver.png`，记录生成提示词和来源。
- [x] 新增 `tools/build-brand-assets.swift`：按指定尺寸栅格化 PNG，生成包含 DIB 与透明掩码的 ICO，以及 iconset/ICNS；保留透明像素。
- [x] 更新 `tools/build-desktop-windows.ps1` 的 `CreateDefaultWin32Resources`，传入 ICO 流并复制资源；`MainForm.cs` 使用 ICO 作为 Form/NotifyIcon 图标、PNG 作为侧栏标识。
- [x] 更新 `SwitchDeskDesktop.nsi` 的安装/卸载图标和资源载荷；在 Main 段 `Call CreateDesktopShortcut`，将图标明确指向安装后的 EXE；卸载删除对应资源。
- [x] 更新 Mac `Main.swift` 侧栏、单色状态栏及构建资源；版本更新到 0.2.10，构建到隔离发行目录。
- [x] 执行两端构建、包完整性/图标资源核验和现有回归，生成 SHA256SUMS 及简短验证记录，交付两个包。

不增加与实现一一对应的字符串断言测试；使用实际构建、资源解析和现有回归验证。本次不触碰实时输入、显示器切源或已安装进程。
