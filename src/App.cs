using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MemoPad
{
    // ───────────────────────── 入口 ─────────────────────────
    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        public static EventWaitHandle ShowSignal;

        public static bool AutoStart;

        // 启动日志：每一步写一行，出问题时能看到停在哪
        public static void Trace(string msg)
        {
            try
            {
                Directory.CreateDirectory(Store.Dir);
                File.AppendAllText(Path.Combine(Store.Dir, "startup.log"), DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + "\r\n");
            }
            catch { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Fatal(e.ExceptionObject as Exception);
            Application.ThreadException += (s, e) => Fatal(e.Exception);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            try { Run(args); }
            catch (Exception ex) { Fatal(ex); }
        }

        // 出错时：写日志 + 弹窗告诉用户原因
        static void Fatal(Exception ex)
        {
            string msg = ex == null ? "未知错误" : ex.ToString();
            Trace("FATAL " + msg);
            try
            {
                Directory.CreateDirectory(Store.Dir);
                File.AppendAllText(Path.Combine(Store.Dir, "error.log"), DateTime.Now + "\r\n" + msg + "\r\n\r\n");
            }
            catch { }
            try { MessageBox.Show("便签本出错了，请把这段文字截图发给 Claude：\n\n" + (ex == null ? msg : ex.GetType().Name + ": " + ex.Message + "\n\n" + ex.StackTrace), "便签本 MemoPad"); } catch { }
        }

        // 如果还有别的版本在运行，结束它，换成当前这个
        static void KillOtherVersions()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return;
            try
            {
                var me = Process.GetCurrentProcess();
                string myPath = Application.ExecutablePath;
                string myVer = FileVersionInfo.GetVersionInfo(myPath).FileVersion ?? "";
                foreach (var p in Process.GetProcessesByName(me.ProcessName).Concat(Process.GetProcessesByName("MemoPad")).GroupBy(x => x.Id).Select(g => g.First()))
                {
                    if (p.Id == me.Id) continue;
                    string path = null, ver = "";
                    try { path = p.MainModule.FileName; ver = FileVersionInfo.GetVersionInfo(path).FileVersion ?? ""; } catch { }
                    bool same = path != null && string.Equals(path, myPath, StringComparison.OrdinalIgnoreCase) && ver == myVer;
                    if (!same) { try { p.Kill(); p.WaitForExit(5000); } catch { } }
                }
            }
            catch { }
        }

        static void Run(string[] args)
        {
            AutoStart = args.Contains("--autostart");
            Trace("==== start v" + Updater.Current + " pid=" + Process.GetCurrentProcess().Id + " args=" + string.Join(" ", args) + " exe=" + Application.ExecutablePath);
            bool created;
            var mutex = new Mutex(true, "MemoPad_SingleInstance_8f3a", out created);
            if (!created && !args.Contains("--restart"))
            {
                KillOtherVersions();   // 旧版本还开着 → 换掉
            }
            if (!created)
            {
                try { created = mutex.WaitOne(args.Contains("--restart") ? 15000 : 1500); } catch (AbandonedMutexException) { created = true; }
            }
            ShowSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "MemoPad_Show_8f3a");
            Trace("mutex created=" + created);
            if (!created) { ShowSignal.Set(); return; }   // 同一个版本已在运行：把它叫到前面

            try { if (Environment.OSVersion.Platform == PlatformID.Win32NT) SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) UI.Dpi = g.DpiX;

            Store.Init();
            Updater.CleanupOld();
            bool firstRun = !File.Exists(Store.StatePath) && !File.Exists(Store.StatePath + ".bak");
            var state = Store.ReadJson<AppState>(Store.StatePath) ?? new AppState();
            if (state.Pages == null) state.Pages = new List<PageInfo>();
            Trace("state loaded firstRun=" + firstRun + " pages=" + state.Pages.Count);
            Fonts.Load(state.FontChoice);
            Trace("fonts face=" + Fonts.Face + " family=" + (Fonts.Family != null ? Fonts.Family.Name : "null"));
            ToolStripManager.Renderer = new PaperRenderer();
            var form = new MainForm(state, firstRun);
            Trace("form constructed, running");
            Application.Run(form);
            Trace("exit");
            GC.KeepAlive(mutex);
        }
    }

    // ───────────────────────── 主窗口 ─────────────────────────
    // 内容铺满整个窗口；页面列表在左边缘、菜单栏在上边缘，鼠标靠近才浮出。
    public class MainForm : Form
    {
        readonly AppState state;
        PageInfo current;
        Control currentView;
        readonly Stack<string> history = new Stack<string>();
        bool reallyExit;

        Panel content, header, host, rule;
        Panel topBar, toolHost, drawer;
        ListBox list;
        TextBox titleBox;
        Button backBtn;
        Label emptyLabel;
        NotifyIcon tray;
        string titleBeforeEdit;
        System.Windows.Forms.Timer guard, hover;
        int leftDwell, topDwell;

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        int TopH { get { return UI.S(42); } }
        int DrawerW { get { return UI.S(220); } }

        public MainForm(AppState state, bool firstRun)
        {
            this.state = state;
            Text = "便签本 MemoPad";
            Font = UI.F(10.5f);
            BackColor = UI.Frame;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            FormBorderStyle = FormBorderStyle.None;     // 无标题栏：不能最小化 / 最大化 / 全屏
            MinimizeBox = false; MaximizeBox = false;
            ShowInTaskbar = false;
            Padding = new Padding(UI.S(4));             // 细边框，拖边缘可调整大小
            MinimumSize = new Size(UI.S(380), UI.S(300));
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;

            Program.Trace("ctor: building UI");
            BuildUI();
            Program.Trace("ctor: UI built");

            if (firstRun) { SeedWelcome(); Program.Trace("ctor: welcome pages created"); }

            var wa = Screen.PrimaryScreen.WorkingArea;
            if (state.W > 200 && state.H > 150 && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new Rectangle(state.X, state.Y, state.W, state.H))))
                Bounds = new Rectangle(state.X, state.Y, state.W, state.H);
            else
                Bounds = new Rectangle(wa.Right - UI.S(560) - UI.S(32), wa.Top + UI.S(32), UI.S(560), Math.Min(UI.S(600), wa.Height - UI.S(64)));

            Program.Trace("ctor: bounds set " + Bounds);
            RefreshList();
            var start = state.Pages.FirstOrDefault(p => p.Id == state.LastPageId) ?? state.Pages.FirstOrDefault();
            if (start != null) OpenPage(start.Id, false); else ShowEmpty();

            Program.Trace("ctor: page opened");
            SetupTray();
            var upd = Updater.JustUpdated();
            if (upd != null) Shown += (s, e) => { try { tray.ShowBalloonTip(3000, "便签本已更新", "现在是 v" + upd, ToolTipIcon.Info); } catch { } };
            StartSignalListener();
            Program.Trace("ctor: tray ready");

            Shown += (s, e) =>
            {
                Program.Trace("shown");
                PlaceOverlays();
                // 开机启动项放到窗口出来之后再写（安全软件拦截也不会卡住窗口）
                BeginInvoke((Action)(() =>
                {
                    try
                    {
                        Program.Trace("startup reg: begin");
                        if (firstRun || IsStartupEnabled()) SetStartup(true);
                        Program.Trace("startup reg: done enabled=" + IsStartupEnabled());
                    }
                    catch (Exception ex) { Program.Trace("startup reg failed: " + ex.Message); }
                }));
                if (Program.AutoStart) Desktop.SendToBottom(this);   // 开机启动：直接待在桌面底层
                else { Desktop.BringUp(this); Activate(); }          // 手动双击：先显示在最前面
            };
            Deactivate += (s, e) => Desktop.SendToBottom(this);   // 用完回到桌面底层

            guard = new System.Windows.Forms.Timer { Interval = 500 };
            int ticks = 0;
            guard.Tick += (s, e) =>
            {
                Desktop.Guard(this);
                if (++ticks % 20 == 1) CheckUpdate();   // 约每 10 秒看一次有没有下载好的新版
                if (ticks == 30 || ticks % 43200 == 0) Updater.CheckOnlineAsync();   // 启动 15 秒后、之后每 6 小时联网检查
            };
            guard.Start();

            hover = new System.Windows.Forms.Timer { Interval = 60 };
            hover.Tick += (s, e) => HoverTick();
            hover.Start();
        }

        // ── 界面 ──
        void BuildUI()
        {
            // 内容：铺满
            content = new Panel { Dock = DockStyle.Fill, BackColor = UI.Bg, Padding = new Padding(UI.S(26), UI.S(18), UI.S(20), UI.S(12)) };
            content.Paint += PaintEdgeHints;
            content.Resize += (s, e) => content.Invalidate();

            header = new Panel { Dock = DockStyle.Top, Height = UI.S(50), BackColor = UI.Bg };
            titleBox = new TextBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, Font = UI.F(22f, FontStyle.Bold), BackColor = UI.Bg, ForeColor = UI.Ink };
            titleBox.Enter += (s, e) => { titleBeforeEdit = current != null ? current.Title : null; };
            titleBox.TextChanged += (s, e) =>
            {
                if (current == null || titleBox.Tag != null) return;
                current.Title = titleBox.Text;
                current.Modified = DateTime.Now;
                list.Invalidate();
            };
            titleBox.Leave += (s, e) => CommitTitle();
            titleBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; CommitTitle(); if (currentView != null) currentView.Focus(); }
            };
            backBtn = UI.TextButton("←", UI.Muted, UI.Bg);
            backBtn.Font = UI.F(15f);
            backBtn.Dock = DockStyle.Left; backBtn.Width = UI.S(34); backBtn.Visible = false;
            backBtn.FlatAppearance.MouseOverBackColor = UI.Selection;
            backBtn.Click += (s, e) => GoBack();
            new ToolTip().SetToolTip(backBtn, "返回上一页 (Alt+←)");
            header.Controls.Add(titleBox);
            header.Controls.Add(backBtn);

            // 标题下的装饰线：一小段朱/靛色 + 一条淡线
            rule = new Panel { Dock = DockStyle.Top, Height = UI.S(14), BackColor = UI.Bg };
            rule.Paint += (s, e) =>
            {
                int y = UI.S(5);
                using (var p = new Pen(UI.Line, 1)) e.Graphics.DrawLine(p, 0, y, rule.Width, y);
                var c = current != null && current.IsMemo ? UI.MemoColor : UI.NoteColor;
                using (var b = new SolidBrush(c)) e.Graphics.FillRectangle(b, 0, y - 1, UI.S(36), UI.S(3));
            };

            host = new Panel { Dock = DockStyle.Fill, BackColor = UI.Bg };
            emptyLabel = new Label
            {
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = UI.Muted, Font = UI.F(12f),
                Text = "还没有页面\n把鼠标移到窗口最上边，点「＋」新建"
            };

            content.Controls.Add(host);
            content.Controls.Add(rule);
            content.Controls.Add(header);
            Controls.Add(content);

            BuildDrawer();
            BuildTopBar();
            Controls.Add(drawer);
            Controls.Add(topBar);
            drawer.BringToFront();
            topBar.BringToFront();
        }

        // 左边缘和上边缘画两道小提示条
        void PaintEdgeHints(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(UI.Line))
            {
                int h = UI.S(44), w = UI.S(4);
                using (var p = UI.RoundRect(new Rectangle(UI.S(3), (content.Height - h) / 2, w, h), w / 2)) g.FillPath(b, p);
                using (var p = UI.RoundRect(new Rectangle((content.Width - h) / 2, UI.S(3), h, w), w / 2)) g.FillPath(b, p);
            }
        }

        // —— 左侧抽屉：页面列表 ——
        void BuildDrawer()
        {
            drawer = new Panel { BackColor = UI.Side, Visible = false, Padding = new Padding(UI.S(10), UI.S(10), UI.S(10), UI.S(10)) };
            drawer.Paint += (s, e) => { using (var p = new Pen(UI.Frame)) e.Graphics.DrawLine(p, drawer.Width - 1, 0, drawer.Width - 1, drawer.Height); };

            var cap = new Label { Dock = DockStyle.Top, Height = UI.S(30), Text = "页 面", Font = UI.F(10f, FontStyle.Bold), ForeColor = UI.Muted, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(UI.S(4), 0, 0, 0) };

            list = new ListBox
            {
                Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = UI.Side,
                DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = UI.S(38), IntegralHeight = false, Font = UI.F(10.5f)
            };
            list.DrawItem += DrawPageItem;
            list.MouseDown += (s, e) =>
            {
                int i = list.IndexFromPoint(e.Location);
                if (i >= 0 && e.Button == MouseButtons.Right) list.SelectedIndex = i;
            };
            list.SelectedIndexChanged += (s, e) =>
            {
                var p = list.SelectedItem as PageInfo;
                if (p != null && (current == null || p.Id != current.Id)) OpenPage(p.Id, true);
            };
            list.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) DeleteCurrent(); };
            list.ContextMenuStrip = BuildListMenu();

            var addRow = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = UI.S(38), ColumnCount = 2, RowCount = 1, BackColor = UI.Side };
            addRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            addRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            var am = UI.TextButton("＋ 备忘录", UI.MemoColor, UI.Side);
            var an = UI.TextButton("＋ 笔记", UI.NoteColor, UI.Side);
            am.Dock = an.Dock = DockStyle.Fill; am.Margin = an.Margin = new Padding(UI.S(2));
            am.Click += (s, e) => NewPage("memo");
            an.Click += (s, e) => NewPage("note");
            addRow.Controls.Add(am, 0, 0);
            addRow.Controls.Add(an, 1, 0);

            drawer.Controls.Add(list);
            drawer.Controls.Add(addRow);
            drawer.Controls.Add(cap);
        }

        ContextMenuStrip BuildListMenu()
        {
            var m = UI.Menu();
            m.Items.Add("重命名", null, (s, e) => { titleBox.Focus(); titleBox.SelectAll(); });
            m.Items.Add("上移", null, (s, e) => MoveCurrent(-1));
            m.Items.Add("下移", null, (s, e) => MoveCurrent(1));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("删除", null, (s, e) => DeleteCurrent()).ForeColor = UI.MemoColor;
            m.Opening += (s, e) => { if (current == null) e.Cancel = true; };
            return m;
        }

        void DrawPageItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var p = (PageInfo)list.Items[e.Index];
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            bool sel = current != null && p.Id == current.Id;
            var color = p.IsMemo ? UI.MemoColor : UI.NoteColor;
            using (var bg = new SolidBrush(UI.Side)) g.FillRectangle(bg, e.Bounds);

            var card = new Rectangle(e.Bounds.X + 1, e.Bounds.Y + UI.S(2), e.Bounds.Width - 3, e.Bounds.Height - UI.S(4));
            if (sel)
            {
                using (var path = UI.RoundRect(card, UI.S(6)))
                using (var b = new SolidBrush(UI.Bg)) g.FillPath(b, path);
                using (var b = new SolidBrush(color)) g.FillRectangle(b, card.X, card.Y + UI.S(8), UI.S(3), card.Height - UI.S(16));
            }

            int dot = UI.S(7);
            var dr = new Rectangle(card.X + UI.S(12), card.Y + (card.Height - dot) / 2, dot, dot);
            using (var b = new SolidBrush(color))
                if (p.IsMemo) g.FillRectangle(b, dr); else g.FillEllipse(b, dr);

            string extra = "";
            if (p.IsMemo)
            {
                var d = MemoCache.Get(p.Id);
                if (d.Items.Count > 0) extra = string.Format("{0}/{1}", d.Items.Count(i => i.Done), d.Items.Count);
            }
            int extraW = 0;
            if (extra != "")
                using (var f = UI.F(9f))
                {
                    extraW = TextRenderer.MeasureText(extra, f).Width;
                    TextRenderer.DrawText(g, extra, f, new Rectangle(card.Right - extraW - UI.S(6), card.Y, extraW, card.Height), UI.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
                }
            var tr = new Rectangle(dr.Right + UI.S(10), card.Y, card.Right - dr.Right - UI.S(18) - extraW, card.Height);
            string title = string.IsNullOrWhiteSpace(p.Title) ? "（无标题）" : p.Title;
            using (var f = UI.F(10.5f, sel ? FontStyle.Bold : FontStyle.Regular))
                TextRenderer.DrawText(g, title, f, tr, UI.Ink, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }

        // —— 顶部浮层：拖动 / 格式工具 / 新建 / 菜单 ——
        void BuildTopBar()
        {
            topBar = new Panel { BackColor = UI.Side, Visible = false };
            topBar.Paint += (s, e) => { using (var p = new Pen(UI.Frame)) e.Graphics.DrawLine(p, 0, topBar.Height - 1, topBar.Width, topBar.Height - 1); };

            var brand = new Panel { Dock = DockStyle.Left, Width = UI.S(104), BackColor = UI.Side, Cursor = Cursors.SizeAll };
            brand.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int sz = UI.S(22);
                var r = new Rectangle(UI.S(12), (brand.Height - sz) / 2, sz, sz);
                using (var path = UI.RoundRect(r, UI.S(4)))
                using (var b = new SolidBrush(UI.Seal)) g.FillPath(b, path);
                using (var f = UI.F(10f, FontStyle.Bold))
                    TextRenderer.DrawText(g, "签", f, r, Color.FromArgb(255, 248, 238), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                using (var f = UI.F(11f, FontStyle.Bold))
                    TextRenderer.DrawText(g, "便签本", f, new Rectangle(r.Right + UI.S(8), 0, brand.Width, brand.Height), UI.Ink, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            };
            MouseEventHandler drag = (s, e) => { if (e.Button == MouseButtons.Left && !state.Locked) Desktop.BeginDrag(this); };
            brand.MouseDown += drag; topBar.MouseDown += drag;

            toolHost = new Panel { Dock = DockStyle.Fill, BackColor = UI.Side, Padding = new Padding(UI.S(4), UI.S(5), 0, UI.S(4)) };
            toolHost.MouseDown += drag;

            var add = BarButton("＋");
            var more = BarButton("⋯");

            var addMenu = UI.Menu();
            addMenu.Items.Add("新建备忘录", null, (s, e) => NewPage("memo")).ForeColor = UI.MemoColor;
            addMenu.Items.Add("新建笔记", null, (s, e) => NewPage("note")).ForeColor = UI.NoteColor;
            add.Click += (s, e) => addMenu.Show(add, new Point(0, add.Height));

            var moreMenu = UI.Menu();
            var miStart = new ToolStripMenuItem("开机自动打开") { CheckOnClick = true };
            miStart.Click += (s, e) => SetStartup(miStart.Checked);
            var miLock = new ToolStripMenuItem("锁定位置和大小") { CheckOnClick = true };
            miLock.Click += (s, e) => { state.Locked = miLock.Checked; SaveState(); };
            var miFont = new ToolStripMenuItem("字体");
            var fWen = new ToolStripMenuItem("霞鹜文楷（默认）", null, (s, e) => SwitchFont(null));
            var fYa = new ToolStripMenuItem("微软雅黑", null, (s, e) => SwitchFont("yahei"));
            miFont.DropDownItems.Add(fWen); miFont.DropDownItems.Add(fYa);
            UI.Track(miFont.DropDown);
            moreMenu.Items.Add(miStart);
            moreMenu.Items.Add(miLock);
            moreMenu.Items.Add(miFont);
            moreMenu.Items.Add(new ToolStripSeparator());
            moreMenu.Items.Add("打开数据文件夹", null, (s, e) => { try { Process.Start("explorer.exe", Store.Dir); } catch { } });
            moreMenu.Items.Add("退出便签本", null, (s, e) => { reallyExit = true; Close(); });
            moreMenu.Items.Add("检查更新", null, (s, e) =>
            {
                Updater.CheckOnlineAsync(r =>
                {
                    try { BeginInvoke((Action)(() => { tray.ShowBalloonTip(3000, "便签本", r, ToolTipIcon.Info); CheckUpdate(); })); } catch { }
                });
            });
            moreMenu.Items.Add(new ToolStripMenuItem("版本 " + Updater.Current.ToString(3)) { Enabled = false });
            moreMenu.Opening += (s, e) =>
            {
                miStart.Checked = IsStartupEnabled(); miLock.Checked = state.Locked;
                fWen.Checked = state.FontChoice != "yahei"; fYa.Checked = state.FontChoice == "yahei";
            };
            more.Click += (s, e) => moreMenu.Show(more, new Point(more.Width - moreMenu.Width, more.Height));

            topBar.Controls.Add(toolHost);
            topBar.Controls.Add(brand);
            topBar.Controls.Add(add);
            topBar.Controls.Add(more);
        }

        Button BarButton(string text)
        {
            var b = UI.TextButton(text, UI.Muted, UI.Side);
            b.Dock = DockStyle.Right; b.Width = UI.S(40);
            b.Font = UI.F(14f, FontStyle.Bold);
            return b;
        }

        void PlaceOverlays()
        {
            var r = DisplayRectangle;
            topBar.SetBounds(r.X, r.Y, r.Width, TopH);
            int y = topBar.Visible ? r.Y + TopH : r.Y;
            drawer.SetBounds(r.X, y, Math.Min(DrawerW, r.Width - UI.S(40)), r.Bottom - y);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (topBar != null) PlaceOverlays();
        }

        // 鼠标靠近边缘 → 浮出；离开 → 收起
        void HoverTick()
        {
            if (!Visible) return;
            var p = PointToClient(Control.MousePosition);
            var r = DisplayRectangle;
            bool inside = ClientRectangle.Contains(p);
            bool busy = UI.MenusOpen > 0 || Control.MouseButtons != MouseButtons.None;
            int zone = UI.S(14);

            leftDwell = inside && p.X <= r.X + zone && p.Y > r.Y + zone ? leftDwell + 1 : 0;
            topDwell = inside && p.Y <= r.Y + zone ? topDwell + 1 : 0;

            if (!topBar.Visible && topDwell >= 2) { topBar.Visible = true; PlaceOverlays(); }
            if (!drawer.Visible && leftDwell >= 2) { PlaceOverlays(); drawer.Visible = true; list.Invalidate(); }

            if (busy) return;
            if (drawer.Visible && (!inside || p.X > drawer.Right + UI.S(24) || (p.Y < drawer.Top - UI.S(4) && !topBar.Visible)))
                drawer.Visible = false;
            if (topBar.Visible && (!inside || p.Y > topBar.Bottom + UI.S(18)) && !(drawer.Visible && p.X <= drawer.Right))
            { topBar.Visible = false; PlaceOverlays(); }
        }

        // ── 页面操作 ──
        public List<PageInfo> Pages { get { return state.Pages; } }
        public AppState State { get { return state; } }

        void RefreshList()
        {
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var p in state.Pages) list.Items.Add(p);
            SyncListSelection();
            list.EndUpdate();
        }

        bool syncing;
        void SyncListSelection()
        {
            syncing = true;
            int idx = -1;
            if (current != null) for (int i = 0; i < list.Items.Count; i++) if (((PageInfo)list.Items[i]).Id == current.Id) idx = i;
            if (list.SelectedIndex != idx) list.SelectedIndex = idx;
            syncing = false;
            list.Invalidate();
        }

        public PageInfo NewPage(string type, string title = null, bool open = true)
        {
            var p = new PageInfo
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 12),
                Type = type,
                Title = title ?? UniqueTitle(type == "memo" ? "新备忘录" : "新笔记"),
                Created = DateTime.Now,
                Modified = DateTime.Now
            };
            int insertAt = current != null ? state.Pages.IndexOf(current) + 1 : state.Pages.Count;
            if (insertAt <= 0 || insertAt > state.Pages.Count) insertAt = state.Pages.Count;
            state.Pages.Insert(insertAt, p);
            if (type == "memo") MemoCache.Save(p.Id, new MemoData { Items = new List<MemoItem>() });
            SaveState();
            RefreshList();
            if (open)
            {
                OpenPage(p.Id, true);
                if (title == null) { titleBox.Focus(); titleBox.SelectAll(); }
            }
            return p;
        }

        string UniqueTitle(string baseName)
        {
            if (!state.Pages.Any(p => p.Title == baseName)) return baseName;
            for (int i = 2; ; i++) if (!state.Pages.Any(p => p.Title == baseName + " " + i)) return baseName + " " + i;
        }

        public void OpenPage(string id, bool pushHistory)
        {
            if (syncing) return;
            var p = state.Pages.FirstOrDefault(x => x.Id == id);
            if (p == null) { MessageBox.Show(this, "这个页面已经不存在了。", "便签本"); return; }
            CommitTitle();
            SaveCurrentView();
            if (pushHistory && current != null && current.Id != id) history.Push(current.Id);

            current = p;
            state.LastPageId = p.Id;

            titleBox.Tag = 1; titleBox.Text = p.Title; titleBox.Tag = null;
            backBtn.Visible = history.Count > 0;

            host.SuspendLayout();
            toolHost.Controls.Clear();
            if (currentView != null) { host.Controls.Remove(currentView); currentView.Dispose(); }
            host.Controls.Remove(emptyLabel);
            if (p.IsMemo) currentView = new MemoView(this, p);
            else
            {
                var nv = new NoteView(this, p);
                toolHost.Controls.Add(nv.Toolbar);
                currentView = nv;
            }
            currentView.Dock = DockStyle.Fill;
            host.Controls.Add(currentView);
            host.ResumeLayout();
            rule.Invalidate();
            SyncListSelection();
            SaveState();
        }

        void ShowEmpty()
        {
            current = null;
            toolHost.Controls.Clear();
            if (currentView != null) { host.Controls.Remove(currentView); currentView.Dispose(); currentView = null; }
            if (!host.Controls.Contains(emptyLabel)) host.Controls.Add(emptyLabel);
            titleBox.Tag = 1; titleBox.Text = ""; titleBox.Tag = null;
            backBtn.Visible = false;
        }

        void GoBack()
        {
            while (history.Count > 0)
            {
                var id = history.Pop();
                if (state.Pages.Any(p => p.Id == id)) { OpenPage(id, false); return; }
            }
            backBtn.Visible = false;
        }

        void CommitTitle()
        {
            if (current == null) return;
            if (string.IsNullOrWhiteSpace(current.Title)) { current.Title = UniqueTitle(current.IsMemo ? "新备忘录" : "新笔记"); titleBox.Tag = 1; titleBox.Text = current.Title; titleBox.Tag = null; }
            if (titleBeforeEdit != null && titleBeforeEdit != current.Title && !current.IsMemo)
                RenameLinksInNotes(titleBeforeEdit, current.Title);
            titleBeforeEdit = current.Title;
            SaveState();
            list.Invalidate();
        }

        // 笔记改名后，把其它笔记里的 [[旧名]] 改成 [[新名]]
        void RenameLinksInNotes(string oldTitle, string newTitle)
        {
            if (string.IsNullOrEmpty(oldTitle)) return;
            string oldTok = "[[" + oldTitle + "]]", newTok = "[[" + newTitle + "]]";
            SaveCurrentView();
            using (var rtb = new RichTextBox())
            {
                foreach (var p in state.Pages.Where(x => !x.IsMemo))
                {
                    var rtf = Store.ReadText(Store.NotePath(p.Id));
                    if (rtf == null) continue;
                    try { rtb.Rtf = rtf; } catch { continue; }
                    if (rtb.Text.IndexOf(oldTok, StringComparison.Ordinal) < 0) continue;
                    int pos, from = 0;
                    while ((pos = rtb.Text.IndexOf(oldTok, from, StringComparison.Ordinal)) >= 0)
                    {
                        rtb.Select(pos, oldTok.Length);
                        rtb.SelectedText = newTok;
                        from = pos + newTok.Length;
                    }
                    Store.WriteText(Store.NotePath(p.Id), rtb.Rtf);
                    var nv = currentView as NoteView;
                    if (nv != null && nv.PageId == p.Id) nv.Reload();
                }
            }
        }

        void MoveCurrent(int dir)
        {
            if (current == null) return;
            int i = state.Pages.IndexOf(current), j = i + dir;
            if (j < 0 || j >= state.Pages.Count) return;
            state.Pages.RemoveAt(i);
            state.Pages.Insert(j, current);
            SaveState();
            RefreshList();
        }

        void DeleteCurrent()
        {
            if (current == null) return;
            var p = current;
            if (MessageBox.Show(this, "确定删除「" + p.Title + "」吗？此操作不能撤销。", "删除页面", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            int idx = state.Pages.IndexOf(p);
            toolHost.Controls.Clear();
            if (currentView != null) { host.Controls.Remove(currentView); currentView.Dispose(); currentView = null; }
            current = null;
            state.Pages.Remove(p);
            Store.DeletePage(p);
            MemoCache.Forget(p.Id);
            SaveState();
            RefreshList();
            if (state.Pages.Count == 0) { ShowEmpty(); return; }
            OpenPage(state.Pages[Math.Min(idx, state.Pages.Count - 1)].Id, false);
        }

        void SeedWelcome()
        {
            var note = NewPage("note", "使用说明 · How to use", false);
            var memo = NewPage("memo", "Today 今天", false);
            var rtf = new RtfBuilder();
            Action<string, float, FontStyle, Color> add = rtf.Add;
            add("欢迎使用便签本\n", 20f, FontStyle.Bold, UI.Ink);
            add("A quiet notebook that lives on your desktop.\n\n", 12f, FontStyle.Italic, UI.Muted);
            add("找东西\n", 15f, FontStyle.Bold, UI.Ink);
            add("· 鼠标移到窗口", 12f, FontStyle.Regular, UI.Ink);
            add("左边缘", 12f, FontStyle.Bold, UI.MemoColor);
            add(" → 浮出页面列表，可以切换、新建页面。\n· 鼠标移到窗口", 12f, FontStyle.Regular, UI.Ink);
            add("上边缘", 12f, FontStyle.Bold, UI.MemoColor);
            add(" → 浮出菜单栏：拖动窗口、排版工具、＋ 新建、⋯ 设置。\n\n", 12f, FontStyle.Regular, UI.Ink);
            add("两种页面\n", 15f, FontStyle.Bold, UI.Ink);
            add("· 备忘录", 12f, FontStyle.Bold, UI.MemoColor);
            add("：一条一条的事项，点圆圈打勾；右键事项 →「链接到页面」，点蓝色标签就跳过去。\n", 12f, FontStyle.Regular, UI.Ink);
            add("· 笔记", 12f, FontStyle.Bold, UI.NoteColor);
            add("：自由排版，支持加粗、标题、颜色、高亮、列表、粘贴图片。笔记里插入的链接像这样 ", 12f, FontStyle.Regular, UI.Ink);
            add("[[Today 今天]]", 12f, FontStyle.Underline, UI.Link);
            add("，按住 Ctrl 再点就会打开。\n\n", 12f, FontStyle.Regular, UI.Ink);
            add("随手算\n", 15f, FontStyle.Bold, UI.Ink);
            add("在笔记或备忘录里打算式，最后敲一个 = ，结果会自动补上：\n", 12f, FontStyle.Regular, UI.Ink);
            add("售价 35*1.6+12=", 12f, FontStyle.Regular, UI.Ink);
            add("68", 12f, FontStyle.Bold, UI.NoteColor);
            add("　　折后 100-20%=", 12f, FontStyle.Regular, UI.Ink);
            add("80", 12f, FontStyle.Bold, UI.NoteColor);
            add("\n支持 + - × ÷ ^ % 和括号，中英文符号都可以。\n\n", 12f, FontStyle.Regular, UI.Ink);
            add("其它\n", 15f, FontStyle.Bold, UI.Ink);
            add("· 它待在桌面最底层，不挡别的窗口；按 Win+D 回到桌面就能看到。\n· 内容自动保存。快捷键：Ctrl+B 加粗，Ctrl+1 / Ctrl+2 标题，Alt+← 返回。\n", 12f, FontStyle.Regular, UI.Ink);
            Store.WriteText(Store.NotePath(note.Id), rtf.ToString());
            MemoCache.Save(memo.Id, new MemoData
            {
                Items = new List<MemoItem>
                {
                    new MemoItem { Text = "Read the guide 看一下使用说明", LinkId = note.Id, Created = DateTime.Now },
                    new MemoItem { Text = "双击一条事项可以修改文字", Created = DateTime.Now },
                    new MemoItem { Text = "Tick the circle when done", Done = true, Created = DateTime.Now },
                }
            });
            state.LastPageId = memo.Id;
            SaveState();
        }

        bool updating;
        void CheckUpdate()
        {
            if (updating || UI.MenusOpen > 0) return;
            if (!Updater.HasUpdate()) return;
            updating = true;
            CommitTitle();
            SaveCurrentView();
            SaveWindow();
            SaveState();
            if (Updater.Apply()) { reallyExit = true; Close(); }
            else updating = false;
        }

        void SwitchFont(string choice)
        {
            if (state.FontChoice == choice) return;
            state.FontChoice = choice;
            SaveState();
            try { Process.Start(Application.ExecutablePath, "--restart"); } catch { }
            reallyExit = true;
            Close();
        }

        // ── 保存 ──
        void SaveCurrentView()
        {
            var nv = currentView as NoteView; if (nv != null) nv.SaveNow();
            var mv = currentView as MemoView; if (mv != null) mv.SaveNow();
        }

        public void PageChanged()
        {
            if (current != null) current.Modified = DateTime.Now;
            list.Invalidate();
        }

        public void SaveState() { try { Store.WriteJson(Store.StatePath, state); } catch { } }

        void SaveWindow()
        {
            if (WindowState == FormWindowState.Normal)
            { var b = Bounds; state.X = b.X; state.Y = b.Y; state.W = b.Width; state.H = b.Height; }
        }

        // ── 开机启动 ──
        static bool IsStartupEnabled()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("MemoPad") != null; }
            catch { return false; }
        }

        static void SetStartup(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue("MemoPad", "\"" + Application.ExecutablePath + "\" --autostart");
                    else if (k.GetValue("MemoPad") != null) k.DeleteValue("MemoPad");
                }
            }
            catch { }
        }

        // ── 托盘 & 单实例 ──
        void SetupTray()
        {
            tray = new NotifyIcon { Icon = Icon ?? SystemIcons.Application, Text = "便签本", Visible = true };
            var m = UI.Menu();
            m.Items.Add("显示便签本", null, (s, e) => ShowMain());
            m.Items.Add("新建备忘录", null, (s, e) => { ShowMain(); NewPage("memo"); });
            m.Items.Add("新建笔记", null, (s, e) => { ShowMain(); NewPage("note"); });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("退出", null, (s, e) => { reallyExit = true; Close(); });
            tray.ContextMenuStrip = m;
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowMain(); };
        }

        void StartSignalListener()
        {
            var t = new Thread(() =>
            {
                while (true)
                {
                    Program.ShowSignal.WaitOne();
                    try { BeginInvoke((Action)ShowMain); } catch { return; }
                }
            }) { IsBackground = true };
            t.Start();
        }

        public void ShowMain()
        {
            Show();
            if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
            Desktop.BringUp(this);
            Activate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            CommitTitle();
            SaveCurrentView();
            SaveWindow();
            SaveState();
            if (!reallyExit && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; return; }  // Alt+F4 关不掉
            tray.Visible = false;
            base.OnFormClosing(e);
        }

        protected override void OnResizeEnd(EventArgs e) { base.OnResizeEnd(e); SaveWindow(); SaveState(); }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Alt | Keys.Left)) { GoBack(); return true; }
            if (keyData == (Keys.Control | Keys.N)) { NewPage("note"); return true; }
            if (keyData == (Keys.Control | Keys.M)) { NewPage("memo"); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        public bool OpenByTitle(string title)
        {
            var p = state.Pages.FirstOrDefault(x => x.Title == title)
                 ?? state.Pages.FirstOrDefault(x => string.Equals((x.Title ?? "").Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase));
            if (p == null)
            {
                if (MessageBox.Show(this, "还没有叫「" + title + "」的页面，要新建一篇笔记吗？", "便签本", MessageBoxButtons.YesNo) == DialogResult.Yes)
                {
                    var n = NewPage("note", title, false);
                    OpenPage(n.Id, true);
                    return true;
                }
                return false;
            }
            OpenPage(p.Id, true);
            return true;
        }

        // ── 无边框 + 桌面层 ──
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80;        // WS_EX_TOOLWINDOW：不进任务栏 / Alt+Tab
                cp.ClassStyle |= 0x20000;  // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Desktop.AttachToDesktop(this);
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x84, WM_SYSCOMMAND = 0x112;
            if (m.Msg == WM_SYSCOMMAND)
            {
                int cmd = m.WParam.ToInt32() & 0xFFF0;
                if (cmd == 0xF020 || cmd == 0xF030 || cmd == 0xF060) return;   // 不允许最小化 / 最大化 / 关闭
            }
            base.WndProc(ref m);
            if (m.Msg == WM_NCHITTEST && !state.Locked && (int)m.Result == 1)
            {
                var p = PointToClient(new Point((short)(m.LParam.ToInt64() & 0xFFFF), (short)((m.LParam.ToInt64() >> 16) & 0xFFFF)));
                int g = UI.S(6);
                bool l = p.X < g, r = p.X >= ClientSize.Width - g, t = p.Y < g, b = p.Y >= ClientSize.Height - g;
                if (t && l) m.Result = (IntPtr)13; else if (t && r) m.Result = (IntPtr)14;
                else if (b && l) m.Result = (IntPtr)16; else if (b && r) m.Result = (IntPtr)17;
                else if (l) m.Result = (IntPtr)10; else if (r) m.Result = (IntPtr)11;
                else if (t) m.Result = (IntPtr)12; else if (b) m.Result = (IntPtr)15;
            }
        }
    }
}
