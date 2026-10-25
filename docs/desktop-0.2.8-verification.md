# 双栖 0.2.8：F12 与 Windows 按钮绘制

## 问题与修改

用户要求单键 F12 切换，并提供 Windows 0.2.7 截图：预览按钮内重复出现主按钮文字、侧栏圆角露紫色块、键鼠来源选项边角发黑。

- Windows 低级键盘钩子在输入转发门控前处理 F12；生产 GlobalHotkeys 状态机消费长按重复和抬起，每次物理按下只触发一次。忽略注入事件的原有钩子过滤保留。Mac 实际 InputBridge.handle 使用 keycode 111，以相同规则去重，并继续检查辅助功能及输入监听权限。旧 Ctrl+Alt/Option+Space 不再切换；紧急恢复组合键保留。
- DeskButton 与 DeskToggle 从 ButtonBase 继承 Opaque，导致透明背景绘制被跳过，自绘又只覆盖圆角内部，留下双缓冲旧像素。关闭这两个控件的 Opaque，恢复父背景重绘。微软 ButtonBase 源码佐证：https://source.dot.net/System.Windows.Forms/System/Windows/Forms/Controls/Buttons/ButtonBase.cs.html 。
- 四类外轮廓按 DPI 笔宽的一半内缩；滚动页在滚动条改变 ClientSize.Width 后最多三轮同步排版，避免横向溢出。
- 两端界面显示 F12；Mac 提示 Fn+F12 或系统标准功能键设置，不拦截音量键。

## 验证结果

- Windows 热键回归先因 F12 未触发失败，修改后通过：首按、长按、抬起、再次按下、修饰键变化、紧急恢复、旧组合键、普通按键与重置。
- Windows UI 回归先观察到背景关闭、125% 起笔画越界和滚动宽度滞后，修改后 3 项通过。构造函数、布局方法及几何表达式直接来自生产源码；WinForms 底层状态和滚动条使用替身，并非真实 GDI 渲染。
- 完整 product Python 回归 55 项通过；Mac 真实私有按键处理器 42 项、armed 状态 7 项与 UIReview 行为隔离测试通过。测试没有安装全局钩子、注入真实按键或发送真实显示器切源命令。
- Mac arm64/macOS13 优化构建与严格签名校验通过；Windows C#5/.NET Framework4.8 x64 全量构建通过，EXE AssemblyVersion 为 0.2.8.0。
- Mac ZIP 与 Windows NSIS 完整性测试通过；Mac ZIP 内为 0.2.8/build10，可执行文件与构建一致；Windows 安装包 EXE、README 与构建一致。哈希见 dist/product/SHA256SUMS-0.2.8.txt。

## 当前设备与交付

已通过界面退出当前 Mac 应用，将原 dist/product/双栖 3.app 备份到 .build/mac-before-0.2.8/双栖 3.app，原路径更新并重开。设置页确认 Mac 0.2.8、Windows 0.2.7.0，连接已恢复，F12 提示可见。辅助功能、输入监听仍未授权，事件钩子未安装；没有代用户授权，因此当前 Mac 实机热键和键鼠共享尚不能启用。

截图记录：dist/product/SwitchDesk-0.2.8-Version-Check.png。

交付：dist/product/SwitchDesk-Windows-0.2.8.exe、dist/product/SwitchDesk-Mac-0.2.8.zip。Windows 需要退出旧托盘进程后更新。没有真实 Windows 运行环境，最终重绘效果和双机 F12 切源仍待用户实机验证；本次没有新的黑屏时长达标证据。未提交或推送。
