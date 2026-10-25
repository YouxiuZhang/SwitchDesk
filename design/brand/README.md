# SwitchDesk · Light Silver

用户选择浅银配色；最终资产由内置 **imagegen** 生成，Swift/AppKit 仅执行平台格式转换和尺寸适配。

- 主图：`switchdesk-silver.png`
- Windows：`native/windows/desktop-assets/SwitchDesk.ico`（16/24/32/48/64/128/256，32 位 BGRA 与透明掩码）及 `SwitchDesk.png`
- Mac：`native/macos/SwitchDesk.icns`、`SwitchDesk.png`
- 格式转换：`xcrun swift tools/build-brand-assets.swift "$PWD"`；沙箱可能阻止 `iconutil`，此时需允许该本地格式转换命令在沙箱外运行。

生成方式：先生成深石墨双窗口图标；用户选浅银后，经 imagegen 调整配色、减轻厚度和外部阴影。最终源文件来自 `exec-6b4c0615-503c-4358-b039-3b0ae8d01db1.png`。

最终提示词：

> Make a new LIGHT SILVER edition of this exact app icon. Preserve the two interlocking rounded square window shapes. The base tile must be pale titanium SILVER GRAY (#D2D5D9), NOT white, with a clearly defined slightly darker smooth outline and continuous rounded-square silhouette. The interlocking window shapes are bright satin platinum silver, with restrained cool gray recessed openings and very subtle blue at the center. Reduce the extrusion and contrast for a lighter more elegant look. Entire icon clearly contained within the central 80% of the canvas with generous empty transparent margin on all sides. NO outside reflections, NO outer glow, NO white highlight specks outside the tile, NO cast shadow outside tile. A clean self-contained cutout, with all rendering strictly inside the shape, the outside must be completely empty transparent. One professional macOS and Windows icon, no text, no grid. Make the tile edge very well defined so the alpha silhouette is clean.

仅保留当前选定资产；旧预览保留在 imagegen 输出目录，不进入产品资源。
