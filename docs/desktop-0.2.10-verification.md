# 0.2.10：浅银图标与 Windows 桌面快捷方式

## 用户选择与修改

用户要求重做应用图标，查看深石墨方案后选择“更轻盈的浅银色”。使用 imagegen 内置工具生成浅银双窗口主图，来源和完整提示词见 `design/brand/README.md`。

Windows 原构建没有向 Roslyn 的 Win32 资源生成器传入图标，托盘使用 `SystemIcons.Application`。桌面快捷方式仅在安装结束页的选项回调中创建。现在向 EXE 嵌入专用 ICO，窗口/任务栏与托盘使用该图标，侧栏使用同源 PNG；NSIS 安装器与卸载器也使用该 ICO。主安装段创建桌面快捷方式，升级与静默安装同样经过该段；卸载只删除本应用资源和快捷方式，配对和显示器配置不在删除清单。

Mac 更新 ICNS、侧栏 PNG 和自适应明暗模式的单色双窗口菜单栏图案。两端版本号为 0.2.10。当前已授权的 Mac 运行实例未覆盖、未重启，以免中断正在使用的跨机键鼠。

## 实际验证

- Windows C#5 / .NET Framework 4.8 x64 全量构建成功，程序集版本 0.2.10.0。
- 解析构建后的 PE 资源：存在 7 个 RT_ICON 及 RT_GROUP_ICON，16/24/32/48/64/128/256 的所有图标帧与 ICO 文件逐字节一致；DIB 尺寸、32 位深度及透明像素校验通过。
- Mac arm64/macOS13 优化构建成功，`codesign --verify --strict` 通过。ICNS 容器及所需尺寸记录有效。`iconutil` 最初被沙箱限制，在沙箱外完成了相同本地图像格式转换。
- Windows NSIS 构建成功；7-Zip 对 EXE 与 Mac ZIP 均返回 `Everything is Ok`。解包 Windows 后 EXE、PNG、ICO、配置与说明逐字节匹配构建产物；Mac ZIP 的版本 0.2.10/build11、可执行文件、ICNS 和 PNG 与构建产物一致。
- 现有产品回归共 67 项：沙箱内运行 65 项通过，2 项 TLS 因回环监听被禁止而报错；在沙箱外单独重跑这 2 项全部通过。没有新增字符串镜像断言测试。
- SHA-256 记录在 `dist/product/SHA256SUMS-0.2.10.txt`。

Windows 桌面快捷方式和任务栏实际显示仍需用户在 Windows 上退出旧托盘进程并安装验证；当前没有 Windows 真机执行证据。没有改变输入转发、显示器切源、网络配对协议或系统权限，没有提交或推送。
