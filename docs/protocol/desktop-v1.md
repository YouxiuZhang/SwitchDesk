# 双栖桌面联动协议 v1（实现契约）

目标是此前已批准的完整双端功能；Windows 0.1.0 已由用户确认能切屏。保留原生视频路径，由 Windows 执行已工作的 DDC，不要求 Mac 的转接器支持 DDC。Mac 通过已配对局域网会话发起切屏；键鼠来源独立可选。

## 连接与身份

Windows 为局域网 TLS 1.2 服务端，TCP 45923（占用时界面明确报错），Mac 为客户端。每个会话两个独立 TLS 连接：`control` 和 `media`。控制/心跳/键鼠不与预览/文件流共用阻塞写锁。每连接 4 字节无符号大端长度 + UTF-8 JSON 对象；control 最大65536字节，media最大12MiB；拒绝零长度、超限、错误JSON及非对象。

Windows 首次生成自签名 RSA2048/SHA256 证书和32字节随机配对秘密，使用当前用户DPAPI保护私钥及秘密。邀请字符串：`switchdesk1:` + 标准Base64(UTF8 JSON `{"host":"192.168.x.x","port":45923,"pin":"小写64位hex证书DER的SHA256","token":"标准base64的32字节秘密"}`)。邀请由Windows界面展示/复制/导出文件，由用户在Mac粘贴/打开。Mac只信任邀请内的精确证书pin；不允许任意证书。Mac保存邀请到Keychain。邀请不得写日志。Windows可撤销配对，轮换token并断开两路。新Mac首次控制连接须Windows窗口点击允许，授权记住该deviceID；拒绝不启动系统能力。仅允许一台配对Mac，换设备须撤销后重新配对。

每路首帧：`{"type":"hello","version":1,"channel":"control|media","token":"...","device":"Mac稳定UUID","name":"MacBook"}`。服务端认证成功回`{"type":"welcome","version":1,"channel":"..."}`；未通过不接收别的帧。media只能在同device已确认control后绑定。双方control每1秒发`{"type":"ping"}`，收到回pong；超过4秒无有效心跳/连接异常时断开两路，释放注入键，停止本地拦截、文件等待和采集。自动重连可以恢复配对与剪贴板，但不能自动恢复远控输入。

## 全局状态（Windows协调）

Windows广播 `{"type":"state","epoch":1,"target":"windows|mac|unknown","source":"windows|mac","ready":true,"can_switch":true,"armed":false,"switching":false,"detail":"..."}`。0.2.2 新增 can_switch，表示已确认显示器映射且无在途操作，可独立切换视频输入，不依赖键鼠权限；Windows 本地切屏也不依赖网络。Mac 按钮还要求已认证连接，旧服务端缺少 can_switch 时回退到 ready。ready仅表示校准已完成且双方输入能力满足；armed是全局显式用户激活门禁。unknown/ready=false/armed=false/switching=true时禁止转发与注入；重连只恢复能力，armed=false，任一端用户切换或选择来源且当前输入核验后才armed=true。Mac信任认证Windows协调的armed，不要求每次必须从Mac本地发起。epoch每次切换/来源变更/断线递增，迟到输入不使用。初始target=windows，ready=false。客户端发送 `switch_request`（可选target，否则toggle）和 `source_request`（source，同值也表示显式请求）。Windows始终串行处理；切换先广播switching=true并release，确认DDC回读及目标输入可用后更新target、epoch、ready、armed、switching=false。失败恢复本地输入、target=unknown，保留明确错误和物理切源提示。首配复用显示器选择及试切确认模式，保存ID/name/original/target输入；重新发现不匹配需重选，不默认17/18映射。

能力：两端发 `{"type":"capabilities","input":true,"capture":false}`，变化立即重发；Mac input要求辅助功能+监听权限均通过且tap安装成功。已配对、ready且本端source==自身且target!=自身时拦截并发送；本端target==自身且source!=自身时接收并注入。紧急键Ctrl+Alt+Esc永远本地处理，停止拦截/释放并发`{"type":"emergency","source":"mac|windows"}`；回到来源的显示器切源最多尝试一次。默认切屏Ctrl+Alt+Space，应用菜单也能触发。每次网络断线和权限丢失均fail-local。

输入 `{"type":"input","epoch":1,"event":{...}}` 在control。事件形式：

- `{"kind":"key","code":65,"down":true}`：code为Windows VK物理键位，Mac适配器维护明确键码表。左右Ctrl/Shift/Alt/Meta用VK 162/163/160/161/164/165/91/92。
- `{"kind":"move","dx":2,"dy":-1}`：相对像素移动，合并可丢弃旧move，不丢key/button/up。
- `{"kind":"button","button":0,"down":true}`：0左1右2中。
- `{"kind":"scroll","dx":0,"dy":-1}`：单位为滚轮刻度（Windows乘120，Mac按3行），允许小数。
- `{"kind":"shortcut","action":"copy|paste|cut|undo|redo|selectAll"}`：可选常用编辑组合映射，接收端用自身Ctrl或Command执行；发送端消费组合键且先释放已转发修饰键。
- `{"kind":"release"}`：释放所有由本应用注入的键和按钮，不释放用户本地按键。

输入适配器有独立3秒租约看门狗，session每次有效心跳刷新；释放消息与断线不依赖UI线程。只注入白名单事件与有限范围dx/dy，忽略自己注入的事件避免回环。

## 剪贴板与文件（media）

消息 `{"type":"clipboard","id":"UUID","origin":"mac|windows","kind":"text|image|files", ...}`。text字段`text`最多1MiB；image字段`data`为PNG标准base64，解码后最多8MiB。files字段`entries`为清单，每项`{"path":"root/sub/file.txt","dir":false,"size":123}`；目录含空目录，最多4096项，总大小最多16GiB。`roots`是所复制顶层名称数组。所有路径必须相对、用/分段，无空段/./../绝对路径/驱动器/反斜杠/NUL；拒绝Windows非法名称、大小写重复、符号链接/重解析点，不能静默跟随。失败在UI明确显示。

剪贴板只在已配对且共享开关开启时轮询，记录本应用写入版本避免回环；不传历史。Windows协调clipboard版本：给每条本地/远端clip分配递增`revision`并回给Mac（含origin）；Mac发的消息revision缺省，等待Windows排序后生效，不用未排序远端内容覆盖新内容。不能把邀请字符串当作自动共享内容。

文字/图片直接在目标原生剪贴板可粘贴。文件清单在目标提供原生延迟 FileDrop / NSPasteboard fileURL provider，Finder/Explorer实际取数据时才请求文件，获取全部文件完成且校验后给出本地缓存路径，由原生文件管理器处理用户粘贴目的地及同名冲突。不得改成只能应用内下载。provider等待用非UI网络队列，不阻塞键鼠网络/钩子；有明确超时/取消失败，不暴露部分文件。

`{"type":"file_request","id":"offer UUID","transfer":"new UUID"}` 仅可请求本端已复制且保留的清单；绝不接受远端任意本地路径。响应顺序：

1. `{"type":"file_begin","id":"...","transfer":"...","entries":[...],"roots":[...]}`（必须与被请求清单一致）。
2. 每个普通文件依序发 `{"type":"file_chunk","transfer":"...","path":"...","offset":0,"data":"base64 32768字节以内"}`；stream写，不整文件入内存。
3. `{"type":"file_end","transfer":"...","path":"...","sha256":"hex"}`；验证大小和hash，空文件也end。
4. `{"type":"file_done","transfer":"..."}`：全部条目已校验才完成并把.partial目录原子重命名到完成目录。

错误 `{"type":"file_error","transfer":"...","detail":"简短原因"}`；取消 `file_cancel` 含transfer。关闭共享/断线取消等待并撤销自身pending剪贴板，不删除用户后来写入的内容。复制新内容不取消已开始传输；保留有上限的旧offer直到无活跃传输。发送前和结束时检查文件size/mtime身份未变。拒绝超限或低磁盘，不解压外来archive。缓存权限当前用户，最多保留会话自己的文件；不执行/打开收到的文件。

## 预览

control `{"type":"preview_subscribe","enabled":true}`：对方窗口可见且开关开启才订阅本机桌面。media帧 `{"type":"preview","data":"JPEGbase64","label":"Windows共享屏幕|Mac外接屏幕|MacBook内置屏幕","width":640,"height":360}`，≤512KiB，约5fps；每路最多一帧等待，文件忙时可丢弃帧。`preview_unavailable`含detail时清空旧帧。接收2秒无新帧移除实时标记；断线/隐藏/暂停/锁屏时停止采集且遮盖旧帧，不录音不存视频。Mac用ScreenCaptureKit优先外接屏，无外接时明确标注内置屏；Windows用所选Screen的GDI捕获，锁屏停采。

## Mac 跨文件API（必须遵守，根文件 Protocol.swift）

`typealias WireMessage = [String: Any]`，辅助 `wireString/wireInt/wireBool`、`validRelativePath`。

系统适配器由NativeCoordinator聚合，所有public UI/session调用在main，网络file处理必须独立后台：

```swift
final class NativeCoordinator {
  var sendControl: ((WireMessage) -> Void)?
  var sendMedia: ((WireMessage) -> Void)?
  var onStatus: ((String) -> Void)?
  var onCapabilities: ((Bool, Bool) -> Void)?
  var onSwitch: (() -> Void)?
  var onEmergency: (() -> Void)?
  var onTransfer: ((String, Double) -> Void)?
  init()
  func start() // 不自动申请权限，不采集未配对剪贴板
  func setConnected(_ connected: Bool)
  func applyState(_ state: WireMessage)
  func receiveControl(_ message: WireMessage)
  func receiveMedia(_ message: WireMessage) // session后台队列调用; 内部安全分派
  func setClipboardEnabled(_ enabled: Bool)
  func setShortcutMapping(_ enabled: Bool)
  func setCaptureAllowed(_ allowed: Bool)
  func requestInputPermissions() // 用户按按钮后调用
  func requestPreviewPermission() // 录屏单独申请
  func refreshPermissions() // 只读检测和重试钩子安装
  func cancelTransfer()
  func heartbeat()
  func stop()
}
```

Mac Session/UI负责TLS、pin、邀请Keychain存储、配对、断线、state、预览显示和UI。NativeCoordinator负责热键/输入/剪贴板文件/本机capture，仅在授权已配对状态工作。`receiveControl`接收input、preview_subscribe；`receiveMedia`接收clipboard/file_*，预览帧由UI处理。`send*`回调不假定main线程；onStatus/onCapabilities/onTransfer回调由UI异步回main。Mac的文件provider不能让网络线程同步等待主线程。

## UI 与交付

两端浅色紫色主按钮，控制台含当前目标、切屏、键鼠来源、另一台预览；共享开关及传输状态；设置包含配对/权限/显示器映射，后台检测无单独页面。关闭窗口保留菜单栏/托盘，菜单含打开/切换/紧急恢复/退出。显式退出停止转发并释放；登录启动默认关。初始离线，不显示假设备/演示图。用户反馈能切屏只确认0.1.0实际试过的接法，不宣称所有线材都已实测。

Mac原生Swift/AppKit包 arm64 macOS13+，Windows C#5/net48 x64，保留现有DDC worker隔离代码。无需为了共享UI引入全新的运行时。版本0.2.0。开发签名/未公证必须如实说明；发布前构建、协议/状态/路径单元测试、包内容检查，Windows无法本机实测的功能不得报已通过。

## 0.2.4 显示器恢复

状态新增 `can_recover_windows`。已认证客户端可发 `{"type":"restore_windows_request"}`，只请求恢复之前用户确认的 Windows 接口；与输入 ready/armed 分离，恢复不启用输入。Windows 用独立映射快照保留恢复目标，并在每次 worker 执行前重新验证身份及当前输入，仍遵守未结束 worker 的写入封锁。失败保持 target=unknown 并显示具体原因，不伪造成功。

`can_switch` 在 target=unknown 且可恢复时仍为 true，兼容旧 Mac 的按钮；此时普通无目标请求也只恢复 Windows。恢复能力不允许再次切向未确认的 Mac。切屏操作结果使用独立操作编号与映射版本关联，输入权限/网络 epoch 变化只解除 armed，不丢弃已返回的物理输入结果。显示器变化通知保留恢复记录；用户改选/刷新/撤销确认则撤销旧记录。

## 0.2.6 切屏回读与画面确认

`state` 增加 `pending_visual_target`（mac/windows/空串）、`visual_confirmation_id`（操作编号，正整数或 0）、`switch_timing_detail`（命令阶段耗时文本）、`input_detail`（键鼠未就绪原因）。Mac 仍按 `ready && armed && !switching` 控制输入，不把 ready 当作已经启用。

只有先前已完成映射确认、近期已成功写入、进程与句柄清理无异常而回读未确认时，Windows 发布画面确认入口。用户明确看到 AOC 外接屏上对应桌面后发送 `{"type":"display_observed","target":"mac","operation":N}`。Windows 校验映射、连接代次、操作编号、两分钟时限和写入封锁后接受，不伪造 DDC 成功；输入保持未 armed。用户随后选择来源才启用，仍需两端输入能力。断线、显示器变化、重新选择、下一次切屏或紧急恢复撤销旧确认。

DDC 一次写入后最多三次回读；250ms 重试间隔与 900ms 软调度预算，第一次成功无等待。同步驱动调用可能超出预算。只有读取失败才尝试一次只读句柄刷新。`ControlResult` 各阶段毫秒值与尝试次数随 worker 返回，`ReadbackUnconfirmed` 只在已成功写入且纯回读未确认时置位，异常与清理失败清除。软件阶段耗时不是实测黑屏时长。

## 0.2.7 延迟状态核对

已成功写入但即时回读未确认时，由 Windows 独立执行最多三次延迟只读查询。仅在结果属于当前显示器映射/操作、回读等于目的接口时更新 target；ready 继续取决于权限与会话，armed 保持 false。成功后发布现有 state，因此按钮自然显示另一个目的地，协议不增加新消息。过期操作、映射变化、阻塞 worker 等不能写回旧结果。
