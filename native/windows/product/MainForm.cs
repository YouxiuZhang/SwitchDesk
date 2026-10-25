using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using SwitchDesk;

namespace SwitchDesk.Desktop {
    public sealed class MainForm : Form {
        private const int WM_DISPLAYCHANGE = 0x007E, WM_WTSSESSION_CHANGE = 0x02B1;
        private const int WTS_SESSION_LOCK = 0x7, WTS_SESSION_UNLOCK = 0x8;
        [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSRegisterSessionNotification(IntPtr window, int flags);
        [DllImport("wtsapi32.dll")] private static extern bool WTSUnRegisterSessionNotification(IntPtr window);
        private readonly Identity identity;
        private readonly Session session;
        private readonly InputEngine input;
        private readonly Coordinator coordinator;
        private readonly FileTransfers files;
        private readonly ClipboardService clipboard;
        private readonly PreviewService preview;
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly Icon brandIcon;
        private readonly Image brandImage;
        private readonly Panel sidebar = new Panel(), workspace = new Panel();
        private readonly Panel[] pages = new Panel[3], contents = new Panel[3];
        private readonly Label[] headings = new Label[3], eyebrows = new Label[3];
        private readonly DeskButton[] navigation = new DeskButton[3];
        private readonly DeskBadge online = new DeskBadge();
        private readonly Label status = new Label(), settingsStatus = new Label(), targetLabel = new Label(), targetDetail = new Label(), pairingLabel = new Label(), transferLabel = new Label(), liveLabel = new Label(), confirmationStatus = new Label(), sourceHint = new Label(), clipboardDetail = new Label(), pageClipboardDetail = new Label(), pageTransferStatus = new Label(), previewEmpty = new Label(), sidebarNote = new Label();
        private readonly PictureBox remotePreview = new PictureBox();
        private readonly ComboBox displays = new ComboBox(), ports = new ComboBox();
        private readonly CheckBox confirm = new CheckBox();
        private readonly DeskToggle share = new DeskToggle(), pageShare = new DeskToggle(), capture = new DeskToggle(), showPreview = new DeskToggle(), shortcut = new DeskToggle();
        private readonly DeskButton switchButton = new DeskButton(), trialButton = new DeskButton(), armButton = new DeskButton(), refreshButton = new DeskButton(), pausePreview = new DeskButton(), zoomPreview = new DeskButton(), detailsButton = new DeskButton();
        private readonly DeskButton windowsSource = new DeskButton(), macSource = new DeskButton();
        private readonly DeskButton observedButton = new DeskButton();
        private readonly Label observationNote = new Label(), timingNote = new Label();
        private string displayedObservationTarget = "";
        private int displayedObservationId;
        private readonly DeskDevice windowsDevice = new DeskDevice(), macDevice = new DeskDevice();
        private DeskPanel hero, sourcePanel, clipboardPanel, shortcutCard, connectionCard, previewCard, dailyCard, clipboardCard, transferCard, monitorFrame;
        private DeskMonitorArt monitorArt;
        private Panel connectionDetails;
        private Label heroLabel, shortcutHint, deviceHeading, deviceNote, sourceHeading, sourceNote, clipboardHeading, footerHint, shortcutTitle, shortcutDescription, switchTitle, switchKeys, emergencyTitle, emergencyKeys, connectionTitle, connectionDescription, displayTitle, portTitle, previewTitle, previewDescription, receivePreviewTitle, receivePreviewNote, sendPreviewTitle, sendPreviewNote, dailyTitle, shortcutMapTitle, shortcutMapNote, clipboardPageTitle, clipboardPageDescription, formatLabel, clipboardEmpty, transferTitle, transferDescription, transferFootnote;
        private DeskButton emergency, invite, revoke, cancelTransfer, pageCancelTransfer;
        private Form enlargedPreview;
        private PictureBox enlargedPicture;
        private Label enlargedState;
        private readonly System.Windows.Forms.Timer uiTimer = new System.Windows.Forms.Timer();
        private readonly List<DisplayInfo> detected = new List<DisplayInfo>();
        private SavedMapping saved;
        private bool loading, exiting, refreshing, layingOut, sharingSync, detailsExpanded;
        private volatile bool previewSubscribed;
        private bool sessionLocked, wtsRegistered;
        private int previewGeneration, displayGeneration, selectedPage;
        private DateTime lastPreview = DateTime.MinValue;
        private readonly Color ink = DeskTheme.Ink;
        private float UiScale { get { return Math.Max(1F, DeviceDpi / 96F); } }
        private int D(float value) { return (int)Math.Round(value * UiScale); }

        public MainForm() {
            identity = new Identity(); session = new Session(identity); input = new InputEngine(); coordinator = new Coordinator(session, input);
            files = new FileTransfers(session); clipboard = new ClipboardService(session, files); preview = new PreviewService(session);
            saved = MappingStore.Load(); detailsExpanded = saved == null || !saved.Confirmed;
            Text = "双栖 · 桌面联动 · " + Application.ProductVersion; BackColor = DeskTheme.Background; Font = DeskTheme.Font(12, false);
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
            ClientSize = new Size(1120, 850); MinimumSize = new Size(780, 560); StartPosition = FormStartPosition.CenterScreen;
            brandIcon = new Icon(Path.Combine(Application.StartupPath, "SwitchDesk.ico"), new Size(32, 32));
            brandImage = Image.FromFile(Path.Combine(Application.StartupPath, "SwitchDesk.png"));
            Icon = brandIcon;
            BuildShell(); BuildConsole(contents[0]); BuildClipboard(contents[1]); BuildSettings(contents[2]);
            ShowPage(0);
            tray.Icon = brandIcon; tray.Text = "双栖桌面联动"; tray.Visible = true;
            var menu = new ContextMenuStrip();
            menu.Items.Add("打开控制台", null, delegate { ShowPage(0); Show(); WindowState = FormWindowState.Normal; Activate(); });
            menu.Items.Add("切换画面", null, delegate { coordinator.RequestSwitch(null); });
            menu.Items.Add("紧急恢复", null, delegate { coordinator.Emergency(); });
            menu.Items.Add("退出", null, delegate { Exit(); });
            tray.ContextMenuStrip = menu; tray.DoubleClick += delegate { Show(); Activate(); };
            session.AskApproval = AskApproval;
            input.Status = SetStatus;
            input.InputUnavailable = delegate { coordinator.SetWindowsInput(false); };
            session.OnStatus += SetStatus;
            session.OnConnected += delegate { Ui(delegate { UpdateUi(); Subscribe(); }); };
            session.OnDisconnected += delegate { clipboard.Disconnect(); preview.SetSubscribed(false); previewSubscribed = false; System.Threading.Interlocked.Increment(ref previewGeneration); Ui(delegate { ClearPreview("连接已断开"); UpdateUi(); }); };
            session.OnControl += ReceiveControl;
            session.OnMedia += ReceiveMedia;
            coordinator.Changed += delegate { Ui(UpdateUi); };
            files.Status += delegate(string message) { Ui(delegate { SetTransferStatus(message); }); };
            clipboard.Status += SetStatus;
            VisibleChanged += delegate { Subscribe(); }; Resize += delegate { Subscribe(); LayoutPages(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (!exiting) { e.Cancel = true; if (enlargedPreview != null) enlargedPreview.Close(); Hide(); Subscribe(); } };
            Shown += async delegate {
                LayoutPages();
                wtsRegistered = WTSRegisterSessionNotification(Handle, 0);
                bool installed = input.Install(); coordinator.SetWindowsInput(installed);
                if (!installed) SetStatus("全局键鼠钩子安装失败，请检查安全软件或权限");
                try { session.Start(); SetStatus("等待 Mac 连接，可在设置中生成配对邀请"); }
                catch (Exception ex) { SetStatus("连接服务启动失败：" + ex.Message); MessageBox.Show(this, status.Text, "连接错误", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                await RefreshDisplays();
            };
            uiTimer.Interval = 500; uiTimer.Tick += delegate { if (lastPreview != DateTime.MinValue && (DateTime.UtcNow - lastPreview).TotalSeconds > 2) ClearPreview("预览已暂停"); }; uiTimer.Start();
            UpdateUi();
        }
        private Label LabelFor(Control owner, string text, float pixels, Color color, bool bold) {
            var label = new Label { Text = text, Font = DeskTheme.Font(pixels, bold), ForeColor = color, BackColor = Color.Transparent, AutoSize = false, UseMnemonic = false };
            owner.Controls.Add(label); return label;
        }
        private void LabelInto(Control owner, Label label, string text, float pixels, Color color, bool bold) { label.Text=text; label.Font=DeskTheme.Font(pixels,bold); label.ForeColor=color; label.BackColor=Color.Transparent; label.UseMnemonic=false; owner.Controls.Add(label); }
        private DeskPanel Card(Control owner) { var card=new DeskPanel(); owner.Controls.Add(card); return card; }
        private DeskButton ActionButton(Control owner, string text, string glyph, DeskButtonStyle kind, EventHandler clicked) { var button=new DeskButton { Text=text, Glyph=glyph, Kind=kind }; owner.Controls.Add(button); button.Click+=clicked; return button; }
        private void AddButton(Control owner, DeskButton button, string text, string glyph, DeskButtonStyle kind, EventHandler clicked) { button.Text=text; button.Glyph=glyph; button.Kind=kind; owner.Controls.Add(button); button.Click+=clicked; }
        private void Put(Control control, float x, float y, float width, float height) { control.SetBounds(D(x),D(y),D(Math.Max(1,width)),D(Math.Max(1,height))); }
        private float TextHeight(Label label, float width, float minimum) { return Math.Max(minimum, TextRenderer.MeasureText(label.Text, label.Font, new Size(D(Math.Max(20,width)), Int32.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height / UiScale + 3); }
        private void BuildShell() {
            sidebar.BackColor=DeskTheme.Sidebar; sidebar.Dock=DockStyle.Left; sidebar.Width=D(188);
            workspace.BackColor=DeskTheme.Background; workspace.Dock=DockStyle.Fill; Controls.Add(workspace); Controls.Add(sidebar);
            var mark=new PictureBox { Image=brandImage, SizeMode=PictureBoxSizeMode.Zoom, BackColor=DeskTheme.Sidebar }; sidebar.Controls.Add(mark); Put(mark,21,22,44,44);
            var brand=LabelFor(sidebar,"双栖",18,DeskTheme.Ink,true); Put(brand,72,24,100,27);
            var brandSub=LabelFor(sidebar,"SWITCHDESK",9,DeskTheme.Muted,false); Put(brandSub,73,51,104,16);
            var caption=LabelFor(sidebar,"工作空间",10,DeskTheme.Muted,false); Put(caption,27,103,140,20);
            string[] names={"控制台","剪贴板与文件","设置"}, glyphs={"console","clipboard","settings"};
            for(int i=0;i<3;i++) { int index=i; navigation[i]=ActionButton(sidebar,names[i],glyphs[i],DeskButtonStyle.Navigation,delegate { ShowPage(index); }); Put(navigation[i],15,132+i*48,158,42); }
            LabelInto(sidebar,sidebarNote,"●  Windows 本机\n双栖在系统托盘中保持运行",10,DeskTheme.Muted,false);
            string[] titles={"两台电脑，一个工作空间。","复制一次，两边可用。","按照你的习惯切换。"}, captions={"YOUR CONNECTED DESK","COPY HERE. PASTE THERE.","MAKE IT YOURS"};
            for(int i=0;i<3;i++) {
                pages[i]=new Panel { Dock=DockStyle.Fill,AutoScroll=true,BackColor=DeskTheme.Background,Visible=false }; workspace.Controls.Add(pages[i]);
                contents[i]=new Panel { BackColor=DeskTheme.Background }; pages[i].Controls.Add(contents[i]);
                eyebrows[i]=LabelFor(contents[i],captions[i],10,DeskTheme.Muted,false); headings[i]=LabelFor(contents[i],titles[i],23,DeskTheme.Ink,true);
                pages[i].ClientSizeChanged+=delegate { LayoutPages(); };
            }
            contents[0].Controls.Add(online); workspace.SizeChanged+=delegate { LayoutPages(); };
        }
        private void ShowPage(int page) {
            selectedPage=page;
            for(int i=0;i<3;i++) { if(pages[i]!=null)pages[i].Visible=i==page; if(navigation[i]!=null)navigation[i].Selected=i==page; }
            if(pages[page]!=null)pages[page].BringToFront();
            LayoutPages(); Subscribe();
        }
        private void BuildConsole(Control page) {
            hero=Card(page); hero.Fill=DeskTheme.Hero; hero.Border=DeskTheme.HeroLine; hero.Radius=15;
            heroLabel=LabelFor(hero,"●  当前控制",11,ColorTranslator.FromHtml("#7c709d"),false);
            LabelInto(hero,targetLabel,"Windows",32,DeskTheme.Ink,true);
            LabelInto(hero,targetDetail,"等待连接与显示器确认",11,ColorTranslator.FromHtml("#8b839e"),false);
            AddButton(hero,switchButton,"切换到 Mac","swap",DeskButtonStyle.Primary,delegate { coordinator.RequestSwitch(null); });
            AddButton(page,observedButton,"确认 AOC 当前画面",null,DeskButtonStyle.Secondary,delegate {
                coordinator.ConfirmDisplayedTarget(displayedObservationTarget,displayedObservationId); UpdateUi();
            });
            LabelInto(page,observationNote,"只在外接 AOC 显示器上看到对应桌面后确认，右侧预览不算。确认后选择键鼠来源。",10,DeskTheme.Muted,false);
            LabelInto(page,timingNote,"",10,DeskTheme.Muted,false);
            shortcutHint=LabelFor(hero,"或按  F12",10,ColorTranslator.FromHtml("#91869e"),false);
            monitorArt=new DeskMonitorArt(); hero.Controls.Add(monitorArt);
            var previewName=LabelFor(monitorArt,"切换前预览 · Mac",10,ColorTranslator.FromHtml("#7c709d"),false); previewName.Dock=DockStyle.Top; previewName.Height=D(20); previewName.TextAlign=ContentAlignment.MiddleCenter;
            monitorFrame=Card(monitorArt); monitorFrame.Fill=ColorTranslator.FromHtml("#2e3040"); monitorFrame.Border=monitorFrame.Fill; monitorFrame.Radius=10;
            remotePreview.SizeMode=PictureBoxSizeMode.Zoom; remotePreview.BackColor=ColorTranslator.FromHtml("#e3e2ed"); monitorFrame.Controls.Add(remotePreview);
            LabelInto(remotePreview,previewEmpty,"预览已关闭\n可点击下方按钮开启",11,DeskTheme.Muted,false); previewEmpty.Dock=DockStyle.Fill; previewEmpty.TextAlign=ContentAlignment.MiddleCenter;
            LabelInto(monitorArt,liveLabel,"尚未订阅",9,ColorTranslator.FromHtml("#8b839e"),false); liveLabel.TextAlign=ContentAlignment.MiddleCenter;
            AddButton(monitorArt,pausePreview,"开启预览","play",DeskButtonStyle.Link,delegate { showPreview.Checked=!showPreview.Checked; });
            AddButton(monitorArt,zoomPreview,"放大","expand",DeskButtonStyle.Link,delegate { OpenPreview(); });
            deviceHeading=LabelFor(page,"我的设备",12,DeskTheme.Ink,true); deviceNote=LabelFor(page,"连接状态实时更新",10,DeskTheme.Muted,false); deviceNote.TextAlign=ContentAlignment.MiddleRight;
            windowsDevice.Text="Windows"; windowsDevice.Glyph="monitor"; windowsDevice.Detail="本机 · "+Environment.MachineName; page.Controls.Add(windowsDevice);
            macDevice.Text="Mac"; macDevice.Glyph="laptop"; page.Controls.Add(macDevice);
            sourcePanel=Card(page); sourceHeading=LabelFor(sourcePanel,"键鼠来源",12,DeskTheme.Ink,true); sourceNote=LabelFor(sourcePanel,"切换画面时保留所选来源",10,DeskTheme.Muted,false); sourceNote.TextAlign=ContentAlignment.MiddleRight;
            AddButton(sourcePanel,windowsSource,"Windows 的键鼠",null,DeskButtonStyle.Source,delegate { coordinator.SetSource("windows"); }); windowsSource.Subtitle="使用本机键盘与鼠标"; windowsSource.AccessibleName="使用 Windows 的键鼠";
            AddButton(sourcePanel,macSource,"Mac 的键盘与触控板",null,DeskButtonStyle.Source,delegate { coordinator.SetSource("mac"); }); macSource.Subtitle="使用 Mac 的输入设备"; macSource.AccessibleName="使用 Mac 的键鼠";
            LabelInto(sourcePanel,sourceHint,"来源设备需保持唤醒",10,DeskTheme.Muted,false);
            AddButton(sourcePanel,armButton,"启用键鼠",null,DeskButtonStyle.Link,delegate { coordinator.SetSource(coordinator.Source); });
            clipboardPanel=Card(page); clipboardHeading=LabelFor(clipboardPanel,"共享剪贴板",12,DeskTheme.Ink,true);
            LabelInto(clipboardPanel,clipboardDetail,"共享已关闭",10,DeskTheme.Muted,false); clipboardPanel.Controls.Add(share); share.AccessibleName="共享剪贴板和文件"; share.CheckedChanged+=delegate { ShareChanged(share); };
            LabelInto(clipboardPanel,transferLabel,"无文件传输",10,DeskTheme.Muted,false);
            cancelTransfer=ActionButton(clipboardPanel,"取消传输",null,DeskButtonStyle.Link,delegate { Task.Run(delegate { files.CancelAll(); }); });
            emergency=ActionButton(page,"紧急收回键鼠","shield",DeskButtonStyle.Link,delegate { coordinator.Emergency(); });
            footerHint=LabelFor(page,"Ctrl + Alt + Esc  ·  收回键鼠，并尝试恢复本机画面",10,DeskTheme.Muted,false); footerHint.TextAlign=ContentAlignment.MiddleRight;
            LabelInto(page,status,"启动中…",10,DeskTheme.Muted,false);
        }
        private void BuildClipboard(Control page) {
            clipboardCard=Card(page); clipboardPageTitle=LabelFor(clipboardCard,"共享剪贴板",15,DeskTheme.Ink,true); clipboardPageDescription=LabelFor(clipboardCard,"在来源电脑复制，在另一台电脑粘贴。",12,DeskTheme.Muted,false);
            clipboardCard.Controls.Add(pageShare); pageShare.AccessibleName="共享剪贴板和文件"; pageShare.CheckedChanged+=delegate { ShareChanged(pageShare); };
            formatLabel=LabelFor(clipboardCard,"文字与链接    ·    图片与截图    ·    文件    ·    文件夹",11,DeskTheme.Violet,false);
            clipboardEmpty=LabelFor(clipboardCard,"共享开启后，按平时的方式复制和粘贴即可。",12,DeskTheme.Muted,false); clipboardEmpty.BackColor=DeskTheme.Sidebar; clipboardEmpty.Padding=new Padding(D(16));
            LabelInto(clipboardCard,pageClipboardDetail,"共享已关闭",11,DeskTheme.Muted,false);
            transferCard=Card(page); transferTitle=LabelFor(transferCard,"文件传输",15,DeskTheme.Ink,true); transferDescription=LabelFor(transferCard,"传输期间，仍可继续切换电脑和使用键鼠。",12,DeskTheme.Muted,false);
            LabelInto(transferCard,pageTransferStatus,"无文件传输",12,DeskTheme.Ink,false);
            pageCancelTransfer=ActionButton(transferCard,"取消传输",null,DeskButtonStyle.Secondary,delegate { Task.Run(delegate { files.CancelAll(); }); });
            transferFootnote=LabelFor(page,"文件和文件夹在目标电脑粘贴时传输。这里显示当前传输的实际状态。",11,DeskTheme.Muted,false);
        }
        private void BuildSettings(Control page) {
            shortcutCard=Card(page); shortcutTitle=LabelFor(shortcutCard,"切换快捷键",15,DeskTheme.Ink,true); shortcutDescription=LabelFor(shortcutCard,"从任意一台电脑切换画面，或随时取回本机键鼠。",11,DeskTheme.Muted,false);
            switchTitle=LabelFor(shortcutCard,"切换 Mac / Windows",12,DeskTheme.Ink,false); switchKeys=LabelFor(shortcutCard,"F12",11,DeskTheme.Violet,false); switchKeys.TextAlign=ContentAlignment.MiddleRight;
            emergencyTitle=LabelFor(shortcutCard,"收回键鼠并尝试恢复本机画面",12,DeskTheme.Ink,false); emergencyKeys=LabelFor(shortcutCard,"Ctrl  +  Alt / ⌥  +  Esc",11,DeskTheme.Violet,false); emergencyKeys.TextAlign=ContentAlignment.MiddleRight;
            connectionCard=Card(page); connectionTitle=LabelFor(connectionCard,"连接",15,DeskTheme.Ink,true); connectionDescription=LabelFor(connectionCard,"配对另一台电脑，并确认显示器的切换方式。",11,DeskTheme.Muted,false);
            LabelInto(connectionCard,pairingLabel,"尚未配对",11,DeskTheme.Muted,false);
            invite=ActionButton(connectionCard,"创建配对邀请",null,DeskButtonStyle.Secondary,delegate { ShowInvitation(); });
            revoke=ActionButton(connectionCard,"撤销配对",null,DeskButtonStyle.Link,delegate { session.Revoke(); UpdateUi(); });
            AddButton(connectionCard,detailsButton,"显示器连接详情", "chevron",DeskButtonStyle.Link,delegate { detailsExpanded=!detailsExpanded; LayoutPages(); });
            connectionDetails=new Panel { BackColor=Color.Transparent }; connectionCard.Controls.Add(connectionDetails);
            displayTitle=LabelFor(connectionDetails,"选择显示器",11,DeskTheme.Ink,true);
            displays.DropDownStyle=ComboBoxStyle.DropDownList; displays.Font=DeskTheme.Font(12,false); connectionDetails.Controls.Add(displays); displays.SelectedIndexChanged+=delegate { SelectionChanged(); };
            AddButton(connectionDetails,refreshButton,"重新检查",null,DeskButtonStyle.Secondary,async delegate { await RefreshDisplays(); });
            portTitle=LabelFor(connectionDetails,"Mac 所在输入口",11,DeskTheme.Ink,false);
            ports.DropDownStyle=ComboBoxStyle.DropDownList; ports.Font=DeskTheme.Font(12,false); ports.Items.Add(new Port { Value=17,Label="HDMI 1" }); ports.Items.Add(new Port { Value=18,Label="HDMI 2" }); ports.Items.Add(new Port { Value=15,Label="DisplayPort" });
            connectionDetails.Controls.Add(ports); ports.SelectedIndexChanged+=delegate { SelectionChanged(); };
            AddButton(connectionDetails,trialButton,"安全试切并自动返回","swap",DeskButtonStyle.Primary,async delegate { SetStatus("试切中，约 10 秒后自动尝试返回"); Task<string> operation=coordinator.TrialAsync(); UpdateUi(); string result=await operation; SetStatus(result); UpdateUi(); });
            confirm.Text="我看到 Mac 画面，并已切回 Windows"; confirm.Font=DeskTheme.Font(11,false); confirm.ForeColor=DeskTheme.Ink; confirm.AutoCheck=false; connectionDetails.Controls.Add(confirm);
            confirm.Click+=async delegate {
                if(loading||!coordinator.CanConfirmObserved)return;
                bool accepted=await coordinator.ConfirmObservedAsync(); UpdateUi();
                if(accepted) { try { MappingStore.Save(coordinator.Mapping); saved=MappingStore.Load(); } catch { confirmationStatus.Text="本次确认已生效，但保存失败；下次启动需重新确认。"; LayoutPages(); } }
            };
            LabelInto(connectionDetails,confirmationStatus,"",11,DeskTheme.Muted,false);
            previewCard=Card(page); previewTitle=LabelFor(previewCard,"另一台电脑的桌面预览",15,DeskTheme.Ink,true); previewDescription=LabelFor(previewCard,"预览用于查看；操作电脑请使用控制台的切换按钮。",11,DeskTheme.Muted,false);
            receivePreviewTitle=LabelFor(previewCard,"开启 Mac 桌面预览",12,DeskTheme.Ink,false); receivePreviewNote=LabelFor(previewCard,"仅在控制台或放大预览可见时接收画面",10,DeskTheme.Muted,false);
            previewCard.Controls.Add(showPreview); showPreview.AccessibleName="开启 Mac 桌面预览"; showPreview.CheckedChanged+=delegate { Subscribe(); };
            sendPreviewTitle=LabelFor(previewCard,"允许 Mac 查看本机屏幕",12,DeskTheme.Ink,false); sendPreviewNote=LabelFor(previewCard,"锁屏、关闭预览或断开连接时停止采集",10,DeskTheme.Muted,false);
            previewCard.Controls.Add(capture); capture.AccessibleName="允许 Mac 查看本机屏幕"; capture.CheckedChanged+=delegate { preview.SetAllowed(capture.Checked); coordinator.SetCapture(capture.Checked); };
            dailyCard=Card(page); dailyTitle=LabelFor(dailyCard,"日常使用",15,DeskTheme.Ink,true); shortcutMapTitle=LabelFor(dailyCard,"跨端常用编辑键映射",12,DeskTheme.Ink,false); shortcutMapNote=LabelFor(dailyCard,"复制、粘贴、剪切、撤销、重做与全选",10,DeskTheme.Muted,false);
            dailyCard.Controls.Add(shortcut); shortcut.AccessibleName="跨端常用编辑键映射"; shortcut.CheckedChanged+=delegate { input.SetShortcutMapping(shortcut.Checked); }; shortcut.Checked=true;
            LabelInto(page,settingsStatus,"",11,DeskTheme.Muted,false);
        }
        private void LayoutPages() {
            if(layingOut||hero==null||dailyCard==null)return;
            layingOut=true;
            try {
                sidebar.Width=D(188); Put(sidebarNote,25,Math.Max(300,sidebar.ClientSize.Height/UiScale-70),146,54);
                for(int i=0;i<3;i++) {
                    // FinishPage can add/remove a vertical scrollbar. Reflow using
                    // the resulting client width before leaving this layout pass.
                    for(int pass=0;pass<3;pass++) {
                        int viewportWidth=pages[i].ClientSize.Width;
                        float width=Math.Max(290,viewportWidth/UiScale-62); float x=31;
                        // All measurements are logical 96-DPI units. Each page scrolls independently.
                        Put(eyebrows[i],x,27,width,18); Put(headings[i],x,47,width-(i==0&&width>=690?145:0),34);
                        if(i==0) LayoutConsole(width,x,width>=690?97:120);
                        else if(i==1)LayoutClipboard(width,x,97);
                        else LayoutSettings(width,x,97);
                        if(pages[i].ClientSize.Width==viewportWidth)break;
                    }
                }
            } finally { layingOut=false; }
        }
        private void FinishPage(int page,float height) { contents[page].Size=new Size(pages[page].ClientSize.Width,D(height)); }
        private void LayoutConsole(float w,float x,float y) {
            bool wide=w>=650; Put(online,wide&&w>=690?x+w-134:x,wide&&w>=690?38:84,134,30);
            float heroHeight=wide?270:496; Put(hero,x,y,w,heroHeight);
            float copyWidth=wide?w*.52F:w;
            Put(heroLabel,27,25,copyWidth-45,22); Put(targetLabel,27,49,copyWidth-45,50);
            Put(targetDetail,27,104,copyWidth-45,38); Put(switchButton,27,151,Math.Min(220,copyWidth-45),40); Put(shortcutHint,27,204,copyWidth-45,22);
            float artWidth=wide?w-copyWidth:w;
            Put(monitorArt,wide?copyWidth:0,wide?8:236,artWidth,254);
            Put(monitorFrame,(artWidth-250)/2,34,250,151); Put(remotePreview,7,7,236,133);
            Put(liveLabel,10,202,artWidth-20,18);
            Put(pausePreview,(artWidth-216)/2,224,126,28); Put(zoomPreview,(artWidth-216)/2+132,224,84,28);
            y+=heroHeight+14;
            if(observedButton.Visible) {
                Put(observedButton,x,y,Math.Min(310,w),36); y+=42;
                float observationHeight=TextHeight(observationNote,w,30); Put(observationNote,x,y,w,observationHeight); y+=observationHeight+10;
            }
            if(timingNote.Visible) { float timingHeight=TextHeight(timingNote,w,22); Put(timingNote,x,y,w,timingHeight); y+=timingHeight+10; }
            y+=8; Put(deviceHeading,x,y,150,20); Put(deviceNote,x+w-175,y,175,20); y+=30;
            bool two=w>=520; float half=(w-12)/2; Put(windowsDevice,x,y,two?half:w,76); Put(macDevice,two?x+half+12:x,two?y:y+88,two?half:w,76); y+=two?92:180;
            float sourceWidth=two?(w-42)/2:w-32; float hintWidth=w-150; float hintHeight=TextHeight(sourceHint,hintWidth,20); float sourceHeight=(two?122:190)+hintHeight;
            Put(sourcePanel,x,y,w,sourceHeight); Put(sourceHeading,16,15,120,20); Put(sourceNote,145,15,w-161,20);
            Put(windowsSource,16,47,sourceWidth,54); Put(macSource,two?26+sourceWidth:16,two?47:111,sourceWidth,54);
            Put(sourceHint,16,two?110:178,hintWidth,hintHeight); Put(armButton,w-122,two?106:174,106,28); y+=sourceHeight+16;
            float transferHeight=TextHeight(transferLabel,w-137,18); float clipHeight=82+transferHeight;
            Put(clipboardPanel,x,y,w,clipHeight); Put(clipboardHeading,16,14,w-100,20); Put(clipboardDetail,16,38,w-85,22); Put(share,w-52,17,36,26);
            Put(transferLabel,16,67,w-137,transferHeight); Put(cancelTransfer,w-112,61,98,29); y+=clipHeight+14;
            float footerHeight=TextHeight(footerHint,w-178,36); Put(emergency,x,y,172,36); Put(footerHint,x+178,y,w-178,footerHeight); y+=footerHeight+9;
            float statusHeight=TextHeight(status,w,26); Put(status,x,y,w,statusHeight); FinishPage(0,y+statusHeight+22);
        }
        private void LayoutClipboard(float w,float x,float y) {
            Put(clipboardCard,x,y,w,267); Put(clipboardPageTitle,22,21,w-96,25); Put(clipboardPageDescription,22,58,w-44,34); Put(pageShare,w-58,21,36,26); Put(formatLabel,22,101,w-44,28); Put(clipboardEmpty,22,143,w-44,58); Put(pageClipboardDetail,22,219,w-44,32);
            y+=283; float stateHeight=TextHeight(pageTransferStatus,w-44,38); float transferHeight=153+stateHeight;
            Put(transferCard,x,y,w,transferHeight); Put(transferTitle,22,21,w-44,26); Put(transferDescription,22,58,w-44,36); Put(pageTransferStatus,22,105,w-44,stateHeight); Put(pageCancelTransfer,w-134,111+stateHeight,112,32);
            y+=transferHeight+20; float noteHeight=TextHeight(transferFootnote,w,40); Put(transferFootnote,x,y,w,noteHeight); FinishPage(1,y+noteHeight+24);
        }
        private void LayoutSettings(float w,float x,float y) {
            Put(shortcutCard,x,y,w,183); Put(shortcutTitle,22,20,w-44,25); Put(shortcutDescription,22,56,w-44,30); Put(switchTitle,22,100,w*.47F-22,23); Put(switchKeys,w*.47F,96,w*.53F-22,30); Put(emergencyTitle,22,144,w*.47F-22,23); Put(emergencyKeys,w*.47F,140,w*.53F-22,30); y+=199;
            float confirmationHeight=TextHeight(confirmationStatus,w-44,36); float detailsHeight=239+confirmationHeight;
            Put(connectionCard,x,y,w,detailsExpanded?175+detailsHeight:166); Put(connectionTitle,22,20,w-44,25); Put(connectionDescription,22,54,w-44,30); Put(pairingLabel,22,88,w-44,23); Put(invite,22,119,142,34); Put(revoke,170,119,103,34); Put(detailsButton,w-190,119,168,34);
            connectionDetails.Visible=detailsExpanded; detailsButton.Text=detailsExpanded?"收起显示器详情":"显示器连接详情"; Put(connectionDetails,0,175,w,detailsHeight);
            Put(displayTitle,22,0,w-44,22); Put(displays,22,29,w-162,30); Put(refreshButton,w-128,27,106,32); Put(portTitle,22,77,132,24); Put(ports,160,73,Math.Min(250,w-182),30); Put(trialButton,22,123,238,40); Put(confirm,22,181,w-44,38); Put(confirmationStatus,22,230,w-44,confirmationHeight); y+=(detailsExpanded?175+detailsHeight:166)+16;
            Put(previewCard,x,y,w,220); Put(previewTitle,22,20,w-44,25); Put(previewDescription,22,56,w-44,34); Put(receivePreviewTitle,22,101,w-90,22); Put(receivePreviewNote,22,128,w-90,28); Put(showPreview,w-58,106,36,26); Put(sendPreviewTitle,22,165,w-90,22); Put(sendPreviewNote,22,190,w-90,23); Put(capture,w-58,170,36,26); y+=236;
            Put(dailyCard,x,y,w,129); Put(dailyTitle,22,20,w-44,25); Put(shortcutMapTitle,22,64,w-90,24); Put(shortcutMapNote,22,91,w-90,25); Put(shortcut,w-58,70,36,26); y+=145;
            float statusHeight=TextHeight(settingsStatus,w,32); Put(settingsStatus,x,y,w,statusHeight); FinishPage(2,y+statusHeight+24);
        }
        private void ShareChanged(DeskToggle sender) {
            if(sharingSync)return; sharingSync=true; share.Checked=pageShare.Checked=sender.Checked; sharingSync=false;
            clipboard.SetEnabled(sender.Checked); UpdateSharingText();
        }
        private void UpdateSharingText() {
            string text=!share.Checked?"共享已关闭 · 不再同步新内容":session.Connected?"文字、图片、文件和文件夹 · 双向共享":"共享已开启 · 等待另一台电脑连接";
            clipboardDetail.Text=pageClipboardDetail.Text=text; LayoutPages();
        }
        private void SetTransferStatus(string message) { transferLabel.Text=pageTransferStatus.Text=message; LayoutPages(); }
        private void OpenPreview() {
            if(enlargedPreview!=null) { enlargedPreview.Activate(); return; }
            enlargedPreview=new Form { Text="Mac · 桌面预览",BackColor=DeskTheme.Background,ClientSize=new Size(D(900),D(575)),MinimumSize=new Size(D(520),D(360)),StartPosition=FormStartPosition.CenterParent,Font=Font,AutoScaleMode=AutoScaleMode.Dpi };
            enlargedPicture=new PictureBox { Dock=DockStyle.Fill,SizeMode=PictureBoxSizeMode.Zoom,BackColor=ColorTranslator.FromHtml("#e3e2ed") };
            enlargedState=new Label { Dock=DockStyle.Bottom,Height=D(48),TextAlign=ContentAlignment.MiddleCenter,ForeColor=DeskTheme.Muted,Font=DeskTheme.Font(11,false) };
            enlargedPreview.Controls.Add(enlargedPicture); enlargedPreview.Controls.Add(enlargedState);
            enlargedPreview.FormClosed+=delegate { enlargedPicture.Image=null; enlargedPreview=null; enlargedPicture=null; enlargedState=null; Subscribe(); };
            enlargedPreview.Resize+=delegate { Subscribe(); };
            SyncEnlargedPreview(); enlargedPreview.Show(this); Subscribe();
        }
        private void SyncEnlargedPreview() { if(enlargedPicture!=null)enlargedPicture.Image=remotePreview.Image; if(enlargedState!=null)enlargedState.Text=liveLabel.Text+"  ·  用于查看，操作电脑请使用主界面的切换按钮"; }
        private sealed class Port { public int Value; public string Label; public override string ToString() { return Label; } }
        private void Ui(Action action) { if (IsDisposed) return; try { if (InvokeRequired) BeginInvoke(action); else action(); } catch { } }
        private string DisplayMessage(string message) {
            if (String.IsNullOrEmpty(message)) return message;
            if (message.StartsWith("显示器回读确认：", StringComparison.Ordinal)) return "显示器画面已确认";
            return message;
        }
        private void SetStatus(string message) { Ui(delegate { status.Text = settingsStatus.Text = DisplayMessage(message); LayoutPages(); }); }
        private bool AskApproval(string device, string name) {
            if (IsDisposed) return false;
            bool approved = false;
            try { Invoke(new Action(delegate { Show(); WindowState = FormWindowState.Normal; Activate(); approved = MessageBox.Show(this,
                "允许这台 Mac 首次连接？\n\n设备：" + name + "\nID：" + device, "双栖 · 配对请求", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes; })); } catch { }
            return approved;
        }
        private void ShowInvitation() {
            try {
                string invitation = identity.Invitation();
                using (var dialog = new Form { Text = "双栖 · Mac 邀请", Width = 660, Height = 240, StartPosition = FormStartPosition.CenterParent, Font = Font }) {
                    var field = new TextBox { Text = invitation, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical }; field.SetBounds(20,20,605,105); dialog.Controls.Add(field);
                    var copy = new Button { Text = "复制邀请" }; copy.SetBounds(20,140,125,30); copy.Click += delegate { Clipboard.SetText(invitation); }; dialog.Controls.Add(copy);
                    var export = new Button { Text = "导出文件" }; export.SetBounds(160,140,125,30); export.Click += delegate { using (var file = new SaveFileDialog { Filter = "双栖邀请 (*.switchdesk)|*.switchdesk", FileName = "Mac邀请.switchdesk" }) if (file.ShowDialog(dialog) == DialogResult.OK) File.WriteAllText(file.FileName, invitation); }; dialog.Controls.Add(export);
                    dialog.ShowDialog(this);
                }
            } catch (Exception ex) { SetStatus("无法生成邀请：" + ex.Message); }
        }
        private async Task RefreshDisplays() {
            if (refreshing || coordinator.DisplayOperationBusy) return; refreshing = true; int generation = displayGeneration; coordinator.InvalidateMapping(); saved = MappingStore.Load(); loading = true; displays.Items.Clear(); displays.SelectedIndex = -1; ports.SelectedIndex = -1; loading = false;
            SetStatus("正在发现显示器…");
            RunReply result = await Task.Run(() => ProcessRunner.Run(20000, false, "--worker", "list"));
            if (generation != displayGeneration) { refreshing = false; return; }
            detected.Clear();
            if (result.Worker != null && result.Worker.Ok && result.Worker.Probe != null) {
                foreach (DisplayInfo info in result.Worker.Probe.Displays) {
                    if (info.Id == null || !info.Id.Contains("/physical-")) continue;
                    detected.Add(info); displays.Items.Add(info.Name + " · " + (info.DdcInput != null && info.DdcInput.Value.HasValue ? "可读取当前输入" : "当前输入不可读取"));
                }
                if (saved != null && saved.Confirmed) {
                    for (int i = 0; i < detected.Count; i++) {
                        DisplayInfo info = detected[i];
                        if (info.Id != saved.Id || info.Name != saved.Name || info.DdcInput == null || (info.DdcInput.Value != saved.Original && info.DdcInput.Value != saved.Target)) continue;
                        loading = true; displays.SelectedIndex = i;
                        for (int j = 0; j < ports.Items.Count; j++) if (((Port)ports.Items[j]).Value == saved.Target) ports.SelectedIndex = j;
                        loading = false;
                        if (ports.SelectedIndex >= 0 && coordinator.RestoreSavedDisplay(info, saved.Original, saved.Target)) { confirm.Checked = true; SetStatus("已验证保存的显示器身份与当前输入，校准可用"); }
                        break;
                    }
                }
                if (displays.SelectedIndex < 0) SetStatus("发现 " + detected.Count + " 台物理显示器，请选择并试切");
            } else SetStatus("显示器读取失败：" + (result.Error ?? "无返回"));
            refreshing = false; UpdateUi();
        }
        private void SelectionChanged() {
            if (loading || refreshing) return;
            coordinator.InvalidateMapping();
            loading = true; confirm.Checked = false; loading = false;
            if (displays.SelectedIndex < 0 || ports.SelectedIndex < 0) { UpdateUi(); return; }
            DisplayInfo info = detected[displays.SelectedIndex]; Port port = (Port)ports.SelectedItem;
            if (info.DdcInput == null || info.DdcInput.Status != "readable" || !info.DdcInput.Value.HasValue) { SetStatus("当前输入无法读取，禁止试切"); return; }
            coordinator.ConfigureDisplay(info, port.Value);
            string device = info.Id.Split('/')[0]; foreach (Screen s in Screen.AllScreens) if (s.DeviceName == device) { preview.SetScreen(s); break; }
            SetStatus("配置已变更，请执行安全试切并确认"); UpdateUi();
        }
        private void UpdateUi() {
            string target=coordinator.Target;
            targetLabel.Text=target=="mac"?"Mac":target=="windows"?"Windows":"画面待确认";
            heroLabel.Text=coordinator.DisplayOperationBusy?"●  正在切换":target=="unknown"?"●  显示器状态待确认":"●  当前控制";
            targetDetail.Text=coordinator.Armed?"显示器画面与键鼠控制已同步":coordinator.Ready?"画面已确认，选择键鼠来源即可启用":target=="unknown"?"键鼠已收回本机，可尝试恢复 Windows":"键鼠保持本机，连接与确认后可共享";
            if(!String.IsNullOrEmpty(coordinator.Detail))status.Text=settingsStatus.Text=DisplayMessage(coordinator.Detail);
            windowsSource.Selected=coordinator.Source=="windows"; macSource.Selected=coordinator.Source=="mac";
            windowsSource.Enabled=macSource.Enabled=coordinator.Ready;
            sourceHint.Text=coordinator.InputDetail;
            string pending=coordinator.PendingVisualTarget;
            displayedObservationTarget=pending; displayedObservationId=coordinator.VisualConfirmationId;
            observedButton.Visible=observationNote.Visible=!String.IsNullOrEmpty(pending);
            observedButton.Text="AOC 已显示 "+(pending=="mac"?"Mac":"Windows")+"，确认画面";
            timingNote.Text=coordinator.SwitchTimingDetail+" · 软件阶段耗时，非黑屏实测";
            timingNote.Visible=!String.IsNullOrEmpty(coordinator.SwitchTimingDetail);
            switchButton.Enabled=coordinator.CanRequestSwitch;
            switchButton.Text=target=="unknown"?"恢复 Windows 画面":target=="mac"?"切换到 Windows":"切换到 Mac";
            armButton.Enabled=coordinator.Ready&&!coordinator.Armed; armButton.Visible=coordinator.Ready&&!coordinator.Armed;
            bool idle=!coordinator.DisplayOperationBusy&&!refreshing;
            displays.Enabled=idle; ports.Enabled=idle; refreshButton.Enabled=idle;
            trialButton.Enabled=idle&&coordinator.Mapping.CanTrial()&&!coordinator.Mapping.TrialReturned&&!ProcessRunner.WriteBlocked;
            confirm.Enabled=idle&&coordinator.CanConfirmObserved&&!coordinator.Mapping.UserConfirmed;
            loading=true; confirm.Checked=coordinator.Mapping.UserConfirmed; loading=false;
            confirmationStatus.Text=coordinator.ConfirmationDetail;
            pairingLabel.Text=session.Connected?"Mac 已连接":identity.Device==null?"尚未配对 · 首次连接需要你允许":"已配对 · Mac 当前离线";
            online.Text=session.Connected?"2 台设备在线":"1 台设备在线"; online.Online=session.Connected; online.Invalidate();
            windowsDevice.Current=target=="windows"; windowsDevice.Badge=target=="windows"?"当前画面":target=="unknown"?"待确认":"本机";
            macDevice.Current=target=="mac"; macDevice.Detail=session.Connected?"已连接":identity.Device==null?"尚未配对":"已配对 · 离线"; macDevice.Badge=target=="mac"?"当前画面":session.Connected?"已连接":"未连接";
            windowsDevice.Invalidate(); macDevice.Invalidate(); UpdateSharingText(); LayoutPages();
        }
        private void ReceiveControl(Dictionary<string, object> message) {
            string type = Wire.S(message, "type");
            if (type == "preview_subscribe") preview.SetSubscribed(Wire.B(message, "enabled"));
            else if (type == "preview_unavailable") { int generation = previewGeneration; Ui(delegate { if (generation == previewGeneration) ClearPreview(Wire.S(message, "detail") ?? "对方预览不可用"); }); }
        }
        private void ReceiveMedia(Dictionary<string, object> message) {
            string type = Wire.S(message, "type");
            if (type == "preview_unavailable") { int generation = previewGeneration; Ui(delegate { if (generation == previewGeneration) ClearPreview(Wire.S(message, "detail") ?? "对方预览不可用"); }); return; }
            if (type == "preview") {
                if (!previewSubscribed) return;
                int generation = previewGeneration;
                try {
                    byte[] bytes = Convert.FromBase64String(Wire.S(message, "data") ?? ""); if (bytes.Length == 0 || bytes.Length > 512 * 1024) return;
                    string label = Wire.S(message, "label"); if (label != "Mac外接屏幕" && label != "MacBook内置屏幕") return;
                    using (var stream = new MemoryStream(bytes)) using (var img = Image.FromStream(stream)) {
                        if (img.RawFormat.Guid != System.Drawing.Imaging.ImageFormat.Jpeg.Guid || img.Width > 1920 || img.Height > 1080) return;
                        Bitmap bitmap = new Bitmap(img);
                        Ui(delegate {
                            if (generation != previewGeneration || !previewSubscribed || !session.Connected || sessionLocked || !Visible) { bitmap.Dispose(); return; }
                            Image old = remotePreview.Image; remotePreview.Image = bitmap; previewEmpty.Visible = false; liveLabel.Text = label + " · 实时"; SyncEnlargedPreview(); if (old != null) old.Dispose(); lastPreview = DateTime.UtcNow;
                        });
                    }
                } catch { }
            } else clipboard.Receive(message);
        }
        private void ClearPreview(string message) {
            Image old=remotePreview.Image; remotePreview.Image=null; liveLabel.Text=message;
            previewEmpty.Text=message; previewEmpty.Visible=true; SyncEnlargedPreview();
            if(old!=null)old.Dispose(); lastPreview=DateTime.MinValue;
        }
        private void Subscribe() {
            bool previewVisible=Visible&&WindowState!=FormWindowState.Minimized&&(selectedPage==0||(enlargedPreview!=null&&enlargedPreview.Visible&&enlargedPreview.WindowState!=FormWindowState.Minimized));
            bool enabled=previewVisible&&!sessionLocked&&showPreview.Checked&&session.Connected;
            bool changed=enabled!=previewSubscribed;
            if(changed)System.Threading.Interlocked.Increment(ref previewGeneration);
            previewSubscribed=enabled;
            if(changed||session.Connected)session.SendControl(Wire.Obj("type","preview_subscribe","enabled",enabled));
            pausePreview.Text=showPreview.Checked?"暂停预览":"开启预览"; pausePreview.Glyph=showPreview.Checked?"pause":"play"; pausePreview.Invalidate();
            if(!enabled)ClearPreview(!showPreview.Checked?"预览已关闭":!session.Connected?"等待 Mac 连接":"预览已暂停");
            else if(changed)ClearPreview("正在等待 Mac 画面");
        }
        protected override void WndProc(ref Message m) {
            if (m.Msg == WM_DISPLAYCHANGE && coordinator != null && !coordinator.DisplayTransitionExpected) {
                displayGeneration++; coordinator.DisplayConfigurationChanged(); UpdateUi();
            } else if (m.Msg == WM_WTSSESSION_CHANGE) {
                if (m.WParam.ToInt32() == WTS_SESSION_LOCK) { sessionLocked = true; Subscribe(); ClearPreview("Windows 已锁屏"); }
                else if (m.WParam.ToInt32() == WTS_SESSION_UNLOCK) { sessionLocked = false; Subscribe(); }
            }
            base.WndProc(ref m);
        }
        private void Exit() { exiting = true; coordinator.SignalRestore(); if (wtsRegistered) WTSUnRegisterSessionNotification(Handle); input.Dispose(); preview.Dispose(); clipboard.Dispose(); session.Dispose(); tray.Visible = false; tray.Dispose(); uiTimer.Stop(); if (enlargedPreview != null) enlargedPreview.Close(); ClearPreview("预览已关闭"); Close(); }
        protected override void Dispose(bool disposing) {
            if (disposing) {
                tray.Dispose();
                if (brandImage != null) brandImage.Dispose();
                if (brandIcon != null) brandIcon.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
