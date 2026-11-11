using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MemoPad
{
    class PaperList : ListView
    {
        public PaperList()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        }
    }

    // ───────────────────────── 备忘录页 ─────────────────────────
    public class MemoView : UserControl
    {
        readonly MainForm main;
        readonly PageInfo page;
        readonly MemoData data;
        PaperList lv;
        TextBox input;
        Font fontNormal, fontDone, fontTag;
        int Gutter { get { return UI.S(32); } }

        public MemoView(MainForm main, PageInfo page)
        {
            this.main = main; this.page = page;
            data = MemoCache.Get(page.Id);
            BackColor = UI.Bg;
            fontNormal = UI.F(12f);
            fontDone = UI.F(12f, FontStyle.Strikeout);
            fontTag = UI.F(9.5f);

            // 输入行：＋ 添加事项
            var top = new Panel { Dock = DockStyle.Top, Height = UI.S(42), BackColor = UI.Bg, Padding = new Padding(UI.S(6), UI.S(10), 0, UI.S(6)) };
            top.Paint += (s, e) => { using (var p = new Pen(UI.Line)) e.Graphics.DrawLine(p, 0, top.Height - 1, top.Width, top.Height - 1); };
            var plus = new Label { Text = "＋", Dock = DockStyle.Left, Width = Gutter - UI.S(6), Font = UI.F(13f, FontStyle.Bold), ForeColor = UI.MemoColor, TextAlign = ContentAlignment.MiddleLeft, BackColor = UI.Bg };
            input = new TextBox { Dock = DockStyle.Fill, Font = UI.F(12f), BorderStyle = BorderStyle.None, BackColor = UI.Bg, ForeColor = UI.Ink };
            SetCue(input, "添加事项，回车确认 · Add an item");
            input.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; AddItem(); } };
            input.KeyPress += (s, e) =>
            {
                if (e.KeyChar != '=' && e.KeyChar != '＝') return;
                BeginInvoke((Action)(() =>
                {
                    int c = input.SelectionStart;
                    if (c == 0 || (input.Text[c - 1] != '=' && input.Text[c - 1] != '＝')) return;
                    if (c < input.Text.Length && !char.IsWhiteSpace(input.Text[c])) return;
                    var r = Calc.TryResult(input.Text.Substring(0, c - 1));
                    if (r != null) input.SelectedText = r;
                }));
            };
            top.Controls.Add(input);
            top.Controls.Add(plus);

            lv = new PaperList
            {
                Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, OwnerDraw = true,
                LabelEdit = true, HideSelection = true, BorderStyle = BorderStyle.None, Font = fontNormal,
                HeaderStyle = ColumnHeaderStyle.None, MultiSelect = true, BackColor = UI.Bg, ForeColor = UI.Ink
            };
            lv.Columns.Add("事项", UI.S(300));
            lv.Columns.Add("链接", UI.S(150));
            lv.SmallImageList = new ImageList { ImageSize = new Size(Gutter, UI.S(40)) };   // 行高 + 左边留出圆圈位置

            lv.DrawColumnHeader += (s, e) => e.DrawDefault = true;
            lv.DrawItem += (s, e) => { };
            lv.DrawSubItem += DrawCell;

            lv.AfterLabelEdit += (s, e) =>
            {
                if (e.Label == null) return;
                var it = lv.Items[e.Item].Tag as MemoItem;
                if (string.IsNullOrWhiteSpace(e.Label)) { e.CancelEdit = true; return; }
                var txt = e.Label.Trim();
                if (txt.EndsWith("=") || txt.EndsWith("＝"))
                {
                    var r = Calc.TryResult(txt.Substring(0, txt.Length - 1));
                    if (r != null)
                    {
                        txt += r;
                        e.CancelEdit = true;
                        var row = lv.Items[e.Item];
                        BeginInvoke((Action)(() => { row.Text = txt; lv.Invalidate(); }));
                    }
                }
                it.Text = txt;
                Changed();
            };
            lv.MouseClick += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                var hit = lv.HitTest(e.Location);
                if (hit.Item == null) return;
                var it = (MemoItem)hit.Item.Tag;
                int x = e.X - hit.Item.Bounds.X;
                if (x < Gutter) { it.Done = !it.Done; Changed(); if (main.State.HideDone && it.Done) Populate(); else lv.Invalidate(hit.Item.Bounds); return; }
                if (x > lv.Columns[0].Width && it.LinkId != null) main.OpenPage(it.LinkId, true);
            };
            lv.MouseDoubleClick += (s, e) =>
            {
                var hit = lv.HitTest(e.Location);
                if (hit.Item == null) return;
                int x = e.X - hit.Item.Bounds.X;
                if (x >= Gutter && x < lv.Columns[0].Width) hit.Item.BeginEdit();
            };
            lv.MouseMove += (s, e) =>
            {
                var hit = lv.HitTest(e.Location);
                Cursor c = Cursors.Default;
                if (hit.Item != null)
                {
                    int x = e.X - hit.Item.Bounds.X;
                    if (x < Gutter || (x > lv.Columns[0].Width && ((MemoItem)hit.Item.Tag).LinkId != null)) c = Cursors.Hand;
                }
                lv.Cursor = c;
            };
            lv.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Delete) DeleteSelected();
                else if (e.KeyCode == Keys.F2 && lv.SelectedItems.Count == 1) lv.SelectedItems[0].BeginEdit();
                else if (e.KeyCode == Keys.Space) { foreach (var it in Selected()) it.Done = !it.Done; Changed(); lv.Invalidate(); e.Handled = true; }
                else if (e.Control && e.KeyCode == Keys.Up) { MoveSelected(-1); e.Handled = true; }
                else if (e.Control && e.KeyCode == Keys.Down) { MoveSelected(1); e.Handled = true; }
            };
            lv.Resize += (s, e) => FitColumns();
            lv.ContextMenuStrip = BuildMenu();

            Controls.Add(lv);
            Controls.Add(top);
            Populate();
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
        static void SetCue(TextBox tb, string cue)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return;
            tb.HandleCreated += (s, e) => { try { SendMessage(tb.Handle, 0x1501, (IntPtr)1, cue); } catch { } };
        }

        void DrawCell(object sender, DrawListViewSubItemEventArgs e)
        {
            var it = (MemoItem)e.Item.Tag;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle cell = e.ColumnIndex == 0
                ? new Rectangle(e.Item.Bounds.X, e.Bounds.Y, lv.Columns[0].Width, e.Bounds.Height)
                : e.Bounds;
            using (var b = new SolidBrush(e.Item.Selected ? UI.Selection : UI.Bg)) g.FillRectangle(b, cell);
            using (var p = new Pen(UI.Line)) g.DrawLine(p, cell.Left, cell.Bottom - 1, cell.Right, cell.Bottom - 1);

            if (e.ColumnIndex == 0)
            {
                int d = UI.S(17);
                var cr = new Rectangle(cell.X + UI.S(6), cell.Y + (cell.Height - d) / 2, d, d);
                if (it.Done)
                {
                    using (var b = new SolidBrush(UI.MemoColor)) g.FillEllipse(b, cr);
                    using (var p = new Pen(Color.FromArgb(255, 248, 238), UI.S(2)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                        g.DrawLines(p, new[] { new Point(cr.X + d * 27 / 100, cr.Y + d * 52 / 100), new Point(cr.X + d * 44 / 100, cr.Y + d * 68 / 100), new Point(cr.X + d * 74 / 100, cr.Y + d * 34 / 100) });
                }
                else
                    using (var p = new Pen(UI.Muted, Math.Max(1.4f, UI.S(15) / 10f))) g.DrawEllipse(p, cr);

                var tr = new Rectangle(cell.X + Gutter, cell.Y, cell.Width - Gutter - UI.S(4), cell.Height);
                TextRenderer.DrawText(g, it.Text, it.Done ? fontDone : fontNormal, tr, it.Done ? UI.Muted : UI.Ink,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            }
            else if (it.LinkId != null)
            {
                var p = main.Pages.FirstOrDefault(x => x.Id == it.LinkId);
                string t = p != null ? "↗ " + p.Title : "链接已失效";
                var c = p == null ? UI.Muted : (p.IsMemo ? UI.MemoColor : UI.NoteColor);
                var sz = TextRenderer.MeasureText(t, fontTag);
                int w = Math.Min(sz.Width + UI.S(16), cell.Width - UI.S(8)), h = UI.S(24);
                var pr = new Rectangle(cell.Right - w - UI.S(4), cell.Y + (cell.Height - h) / 2, w, h);
                using (var path = UI.RoundRect(pr, h / 2))
                using (var b = new SolidBrush(Color.FromArgb(28, c))) g.FillPath(b, path);
                var inner = new Rectangle(pr.X + UI.S(8), pr.Y, pr.Width - UI.S(14), pr.Height);
                TextRenderer.DrawText(g, t, fontTag, inner, c, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            }
        }

        void FitColumns()
        {
            int w = lv.ClientSize.Width;
            int lw = data.Items.Any(i => i.LinkId != null) ? Math.Min(UI.S(170), w * 2 / 5) : UI.S(8);
            lv.Columns[1].Width = lw;
            lv.Columns[0].Width = Math.Max(UI.S(100), w - lw - 2);
        }

        void Populate()
        {
            lv.BeginUpdate();
            lv.Items.Clear();
            foreach (var it in data.Items)
            {
                if (main.State.HideDone && it.Done) continue;
                var row = new ListViewItem(it.Text) { Tag = it };
                row.SubItems.Add("");
                lv.Items.Add(row);
            }
            lv.EndUpdate();
            FitColumns();
        }

        void AddItem()
        {
            var t = input.Text.Trim();
            if (t == "") return;
            data.Items.Add(new MemoItem { Text = t, Created = DateTime.Now });
            input.Text = "";
            Changed();
            Populate();
            if (lv.Items.Count > 0) lv.EnsureVisible(lv.Items.Count - 1);
        }

        List<MemoItem> Selected() { return lv.SelectedItems.Cast<ListViewItem>().Select(r => (MemoItem)r.Tag).ToList(); }

        void DeleteSelected()
        {
            var sel = Selected(); if (sel.Count == 0) return;
            foreach (var it in sel) data.Items.Remove(it);
            Changed(); Populate();
        }

        void MoveSelected(int dir)
        {
            var sel = Selected(); if (sel.Count != 1) return;
            var it = sel[0];
            var visible = data.Items.Where(x => !(main.State.HideDone && x.Done)).ToList();
            int vi = visible.IndexOf(it), vj = vi + dir;
            if (vj < 0 || vj >= visible.Count) return;
            var other = visible[vj];
            int a = data.Items.IndexOf(it), b = data.Items.IndexOf(other);
            data.Items[a] = other; data.Items[b] = it;
            Changed(); Populate();
            foreach (ListViewItem r in lv.Items) if (r.Tag == it) { r.Selected = true; r.Focused = true; r.EnsureVisible(); }
        }

        ContextMenuStrip BuildMenu()
        {
            var m = UI.Menu();
            var edit = new ToolStripMenuItem("编辑文字", null, (s, e) => { if (lv.SelectedItems.Count == 1) lv.SelectedItems[0].BeginEdit(); }) { ShortcutKeyDisplayString = "F2" };
            var openLink = new ToolStripMenuItem("打开链接", null, (s, e) =>
            {
                var sel = Selected(); if (sel.Count == 1 && sel[0].LinkId != null) main.OpenPage(sel[0].LinkId, true);
            });
            var linkTo = new ToolStripMenuItem("链接到页面");
            UI.Track(linkTo.DropDown);
            var newNote = new ToolStripMenuItem("新建笔记并链接", null, (s, e) => CreateLinkedNote());
            var unlink = new ToolStripMenuItem("取消链接", null, (s, e) => { foreach (var it in Selected()) it.LinkId = null; Changed(); Populate(); });
            var toggle = new ToolStripMenuItem("完成 / 未完成", null, (s, e) => { foreach (var it in Selected()) it.Done = !it.Done; Changed(); Populate(); }) { ShortcutKeyDisplayString = "空格" };
            var up = new ToolStripMenuItem("上移", null, (s, e) => MoveSelected(-1)) { ShortcutKeyDisplayString = "Ctrl+↑" };
            var down = new ToolStripMenuItem("下移", null, (s, e) => MoveSelected(1)) { ShortcutKeyDisplayString = "Ctrl+↓" };
            var hide = new ToolStripMenuItem("隐藏已完成") { CheckOnClick = true };
            hide.Click += (s, e) => { main.State.HideDone = hide.Checked; main.SaveState(); Populate(); };
            var clearDone = new ToolStripMenuItem("清除所有已完成", null, (s, e) =>
            {
                if (MessageBox.Show(this, "删除这一页所有已完成的事项？", "便签本", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                data.Items.RemoveAll(x => x.Done); Changed(); Populate();
            });
            var del = new ToolStripMenuItem("删除", null, (s, e) => DeleteSelected()) { ShortcutKeyDisplayString = "Del", ForeColor = UI.MemoColor };

            m.Items.AddRange(new ToolStripItem[] { edit, openLink, linkTo, newNote, unlink, new ToolStripSeparator(), toggle, up, down, new ToolStripSeparator(), hide, clearDone, del });

            m.Opening += (s, e) =>
            {
                var sel = Selected();
                bool one = sel.Count == 1, any = sel.Count > 0;
                edit.Visible = one; up.Visible = one; down.Visible = one;
                linkTo.Visible = any; newNote.Visible = one; toggle.Visible = any; del.Visible = any;
                openLink.Visible = one && sel[0].LinkId != null;
                unlink.Visible = any && sel.Any(x => x.LinkId != null);
                hide.Checked = main.State.HideDone;
                clearDone.Enabled = data.Items.Any(x => x.Done);

                linkTo.DropDownItems.Clear();
                var others = main.Pages.Where(p => p.Id != page.Id).OrderBy(p => p.IsMemo).ToList();
                if (others.Count == 0) linkTo.DropDownItems.Add(new ToolStripMenuItem("（还没有其它页面）") { Enabled = false });
                foreach (var p in others)
                {
                    var target = p;
                    var mi = new ToolStripMenuItem(p.Title) { ForeColor = p.IsMemo ? UI.MemoColor : UI.NoteColor };
                    if (one && sel[0].LinkId == p.Id) mi.Checked = true;
                    mi.Click += (s2, e2) => { foreach (var it in Selected()) it.LinkId = target.Id; Changed(); Populate(); };
                    linkTo.DropDownItems.Add(mi);
                }
            };
            return m;
        }

        void CreateLinkedNote()
        {
            var sel = Selected(); if (sel.Count != 1) return;
            var it = sel[0];
            string title = it.Text.Length > 40 ? it.Text.Substring(0, 40) : it.Text;
            if (main.Pages.Any(p => p.Title == title)) title = title + " · 笔记";
            var note = main.NewPage("note", title, false);
            it.LinkId = note.Id;
            Changed();
            main.OpenPage(note.Id, true);
        }

        void Changed()
        {
            MemoCache.Save(page.Id, data);
            main.PageChanged();
            main.SaveState();
        }

        public void SaveNow() { MemoCache.Save(page.Id, data); }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); input.Focus(); }
    }

    // ───────────────────────── 笔记页 ─────────────────────────
    public class NoteView : UserControl
    {
        readonly MainForm main;
        readonly PageInfo page;
        RichTextBox rtb;
        System.Windows.Forms.Timer saveTimer;
        bool dirty, loading;
        public ToolStrip Toolbar;
        public string PageId { get { return page.Id; } }
        static bool Win { get { return Environment.OSVersion.Platform == PlatformID.Win32NT; } }

        public NoteView(MainForm main, PageInfo page)
        {
            this.main = main; this.page = page;
            BackColor = UI.Bg;
            Padding = new Padding(UI.S(2), UI.S(8), 0, 0);

            rtb = new RichTextBox
            {
                Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, Font = UI.F(12f), AcceptsTab = true,
                DetectUrls = true, HideSelection = false, BackColor = UI.Bg, ForeColor = UI.Ink,
                ScrollBars = RichTextBoxScrollBars.Vertical
            };
            Controls.Add(rtb);
            Toolbar = BuildToolbar();

            Reload();

            saveTimer = new System.Windows.Forms.Timer { Interval = 800 };
            saveTimer.Tick += (s, e) => { saveTimer.Stop(); SaveNow(); };
            rtb.TextChanged += (s, e) =>
            {
                if (loading) return;
                dirty = true; saveTimer.Stop(); saveTimer.Start();
            };
            rtb.LinkClicked += (s, e) => { try { Process.Start(e.LinkText); } catch { } };
            rtb.MouseUp += OnRtbMouseUp;
            rtb.MouseMove += (s, e) =>
            {
                rtb.Cursor = (ModifierKeys == Keys.Control && LinkAt(rtb.GetCharIndexFromPosition(e.Location)) != null) ? Cursors.Hand : Cursors.IBeam;
            };
            rtb.KeyDown += OnKeyDown;
            rtb.KeyPress += (s, e) => { if (e.KeyChar == '=' || e.KeyChar == '＝') BeginInvoke((Action)AfterEquals); };
        }

        // 随手算：输入 "算式=" 后自动补结果
        void AfterEquals()
        {
            int c = rtb.SelectionStart;
            var text = rtb.Text;
            if (c == 0 || c > text.Length || (text[c - 1] != '=' && text[c - 1] != '＝') || rtb.SelectionLength > 0) return;
            if (c < text.Length && text[c] != '\n' && !char.IsWhiteSpace(text[c])) return;
            int ls = rtb.GetFirstCharIndexFromLine(rtb.GetLineFromCharIndex(c - 1));
            if (ls < 0 || ls > c - 1) return;
            var r = Calc.TryResult(text.Substring(ls, c - 1 - ls));
            if (r == null) return;
            var oldColor = rtb.SelectionColor;
            bool wasBold = HasEffect(CFM_BOLD);
            rtb.SelectionColor = UI.NoteColor;
            SetCF(CFM_BOLD, CFM_BOLD);
            rtb.SelectedText = r;
            rtb.SelectionColor = oldColor.IsEmpty ? UI.Ink : oldColor;
            SetCF(CFM_BOLD, wasBold ? CFM_BOLD : 0);
        }

        public void Reload()
        {
            loading = true;
            var rtf = Store.ReadText(Store.NotePath(page.Id));
            if (rtf != null) { try { rtb.Rtf = rtf; } catch { rtb.Text = rtf; } }
            else rtb.Clear();
            rtb.Select(0, 0);
            loading = false;
            dirty = false;
        }

        // ── 富文本格式（直接用 RichEdit 消息，保证字体不被换掉） ──
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct CHARFORMAT2
        {
            public int cbSize; public uint dwMask; public uint dwEffects; public int yHeight; public int yOffset; public int crTextColor;
            public byte bCharSet; public byte bPitchAndFamily;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szFaceName;
            public short wWeight; public short sSpacing; public int crBackColor; public int lcid; public int dwReserved;
            public short sStyle; public short wKerning; public byte bUnderlineType; public byte bAnimation; public byte bRevAuthor; public byte bUnderlineColor;
        }
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, ref CHARFORMAT2 cf);
        const int EM_GETCHARFORMAT = 0x43A, EM_SETCHARFORMAT = 0x444;
        const uint CFM_BOLD = 1, CFM_ITALIC = 2, CFM_UNDERLINE = 4, CFM_STRIKEOUT = 8, CFM_FACE = 0x20000000, CFM_SIZE = 0x80000000;

        CHARFORMAT2 NewCF() { return new CHARFORMAT2 { cbSize = Marshal.SizeOf(typeof(CHARFORMAT2)), szFaceName = "" }; }

        void SetCF(uint mask, uint effects, float size = 0, string face = null)
        {
            if (Win)
            {
                var cf = NewCF();
                cf.dwMask = mask; cf.dwEffects = effects;
                if (size > 0) cf.yHeight = (int)(size * 20);
                if (face != null) cf.szFaceName = face;
                SendMessage(rtb.Handle, EM_SETCHARFORMAT, (IntPtr)1 /* SCF_SELECTION */, ref cf);
                return;
            }
            // 非 Windows 测试环境的退路
            var f = rtb.SelectionFont ?? rtb.Font;
            var st = f.Style;
            foreach (var pair in new[] { Tuple.Create(CFM_BOLD, FontStyle.Bold), Tuple.Create(CFM_ITALIC, FontStyle.Italic), Tuple.Create(CFM_UNDERLINE, FontStyle.Underline), Tuple.Create(CFM_STRIKEOUT, FontStyle.Strikeout) })
                if ((mask & pair.Item1) != 0) st = (effects & pair.Item1) != 0 ? st | pair.Item2 : st & ~pair.Item2;
            rtb.SelectionFont = UI.F(size > 0 ? size : f.Size, st);
        }

        bool HasEffect(uint mask)
        {
            if (Win)
            {
                var cf = NewCF();
                SendMessage(rtb.Handle, EM_GETCHARFORMAT, (IntPtr)1, ref cf);
                return (cf.dwMask & mask) != 0 && (cf.dwEffects & mask) != 0;
            }
            var f = rtb.SelectionFont ?? rtb.Font;
            if (mask == CFM_BOLD) return f.Bold;
            if (mask == CFM_ITALIC) return f.Italic;
            if (mask == CFM_UNDERLINE) return f.Underline;
            return f.Strikeout;
        }

        void Toggle(uint mask) { SetCF(mask, HasEffect(mask) ? 0 : mask); rtb.Focus(); }

        ToolStrip BuildToolbar()
        {
            var ts = new ToolStrip
            {
                Dock = DockStyle.Fill, GripStyle = ToolStripGripStyle.Hidden, BackColor = UI.Side, Font = UI.F(10.5f),
                Padding = new Padding(0), CanOverflow = true, AutoSize = false
            };
            Func<string, string, FontStyle, EventHandler, ToolStripButton> btn = (text, tip, st, h) =>
            {
                var b = new ToolStripButton(text) { ToolTipText = tip, Font = UI.F(11f, st), AutoSize = false, Width = UI.S(30), Height = UI.S(28) };
                b.Click += h;
                return b;
            };
            ts.Items.Add(btn("B", "加粗 Ctrl+B", FontStyle.Bold, (s, e) => Toggle(CFM_BOLD)));
            ts.Items.Add(btn("I", "斜体 Ctrl+I", FontStyle.Italic, (s, e) => Toggle(CFM_ITALIC)));
            ts.Items.Add(btn("U", "下划线 Ctrl+U", FontStyle.Underline, (s, e) => Toggle(CFM_UNDERLINE)));
            ts.Items.Add(new ToolStripSeparator());

            var head = new ToolStripDropDownButton("段落") { ToolTipText = "标题 / 正文" };
            head.DropDownItems.Add(new ToolStripMenuItem("大标题", null, (s, e) => SetBlock(20f, true)) { ShortcutKeyDisplayString = "Ctrl+1", Font = UI.F(14f, FontStyle.Bold) });
            head.DropDownItems.Add(new ToolStripMenuItem("小标题", null, (s, e) => SetBlock(15f, true)) { ShortcutKeyDisplayString = "Ctrl+2", Font = UI.F(12f, FontStyle.Bold) });
            head.DropDownItems.Add(new ToolStripMenuItem("正文", null, (s, e) => SetBlock(12f, false)) { ShortcutKeyDisplayString = "Ctrl+0" });
            UI.Track(head.DropDown);
            ts.Items.Add(head);

            var color = new ToolStripDropDownButton("颜色") { ToolTipText = "文字颜色 / 高亮" };
            AddColorItems(color);
            UI.Track(color.DropDown);
            ts.Items.Add(color);

            ts.Items.Add(new ToolStripButton("列表", null, (s, e) => { rtb.SelectionBullet = !rtb.SelectionBullet; rtb.Focus(); }) { ToolTipText = "项目符号列表" });
            ts.Items.Add(new ToolStripButton("☐", null, (s, e) => InsertCheck()) { ToolTipText = "行首插入 ☐，再点变 ☑" });

            var link = new ToolStripDropDownButton("链接") { ToolTipText = "插入指向其它页面的链接" };
            UI.Track(link.DropDown);
            link.DropDownOpening += (s, e) =>
            {
                link.DropDownItems.Clear();
                foreach (var p in main.Pages.Where(x => x.Id != page.Id))
                {
                    var t = p;
                    link.DropDownItems.Add(new ToolStripMenuItem(p.Title, null, (s2, e2) => InsertLink(t.Title)) { ForeColor = p.IsMemo ? UI.MemoColor : UI.NoteColor });
                }
                if (link.DropDownItems.Count > 0) link.DropDownItems.Add(new ToolStripSeparator());
                link.DropDownItems.Add("新建笔记并链接…", null, (s2, e2) =>
                {
                    var name = Prompt("新笔记的标题：", rtb.SelectedText.Trim());
                    if (string.IsNullOrWhiteSpace(name)) return;
                    if (!main.Pages.Any(x => x.Title == name)) main.NewPage("note", name, false);
                    InsertLink(name);
                });
            };
            ts.Items.Add(link);
            foreach (ToolStripItem i in ts.Items) i.Margin = new Padding(UI.S(1), 0, UI.S(1), 0);
            return ts;
        }

        void AddColorItems(ToolStripDropDownButton dd)
        {
            var ink = new[] {
                Tuple.Create("墨色", UI.Ink), Tuple.Create("朱红", UI.MemoColor), Tuple.Create("靛青", UI.NoteColor),
                Tuple.Create("松绿", Color.FromArgb(70, 120, 80)), Tuple.Create("赭石", Color.FromArgb(170, 110, 40)), Tuple.Create("灰", UI.Muted) };
            var hl = new[] {
                Tuple.Create("鹅黄高亮", Color.FromArgb(250, 230, 150)), Tuple.Create("浅绿高亮", Color.FromArgb(205, 230, 195)),
                Tuple.Create("浅蓝高亮", Color.FromArgb(205, 222, 238)), Tuple.Create("藕粉高亮", Color.FromArgb(240, 208, 205)), Tuple.Create("清除高亮", UI.Bg) };
            foreach (var c in ink) AddSwatch(dd, c.Item1, c.Item2, false);
            dd.DropDownItems.Add(new ToolStripSeparator());
            foreach (var c in hl) AddSwatch(dd, c.Item1, c.Item2, true);
        }

        void AddSwatch(ToolStripDropDownButton dd, string name, Color col, bool back)
        {
            var bmp = new Bitmap(UI.S(14), UI.S(14));
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(col);
                using (var p = new Pen(UI.Frame)) g.DrawRectangle(p, 0, 0, bmp.Width - 1, bmp.Height - 1);
            }
            dd.DropDownItems.Add(name, bmp, (s, e) =>
            {
                if (back) rtb.SelectionBackColor = col; else rtb.SelectionColor = col;
                rtb.Focus();
            });
        }

        void SetBlock(float size, bool bold)
        {
            int keep = rtb.SelectionStart, keepLen = rtb.SelectionLength;
            int start = rtb.GetFirstCharIndexFromLine(rtb.GetLineFromCharIndex(rtb.SelectionStart));
            int endLine = rtb.GetLineFromCharIndex(rtb.SelectionStart + rtb.SelectionLength);
            int end = endLine + 1 < rtb.Lines.Length ? rtb.GetFirstCharIndexFromLine(endLine + 1) - 1 : rtb.TextLength;
            if (end < start) end = start;
            rtb.Select(start, end - start);
            SetCF(CFM_SIZE | CFM_BOLD, bold ? CFM_BOLD : 0, size);
            rtb.Select(keep, keepLen);
            if (keepLen == 0) SetCF(CFM_SIZE | CFM_BOLD, bold ? CFM_BOLD : 0, size);
            rtb.Focus();
        }

        void InsertCheck()
        {
            int line = rtb.GetLineFromCharIndex(rtb.SelectionStart);
            int ls = rtb.GetFirstCharIndexFromLine(line);
            string text = rtb.Text;
            int keep = rtb.SelectionStart;
            if (ls < text.Length && (text[ls] == '☐' || text[ls] == '☑'))
            {
                rtb.Select(ls, 1);
                rtb.SelectedText = text[ls] == '☐' ? "☑" : "☐";
                rtb.Select(keep, 0);
            }
            else
            {
                rtb.Select(ls, 0);
                rtb.SelectedText = "☐ ";
                rtb.Select(keep + 2, 0);
            }
            rtb.Focus();
        }

        void InsertLink(string title)
        {
            rtb.SelectionColor = UI.Link;
            SetCF(CFM_UNDERLINE, CFM_UNDERLINE);
            rtb.SelectedText = "[[" + title + "]]";
            rtb.SelectionColor = UI.Ink;
            SetCF(CFM_UNDERLINE, 0);
            rtb.SelectedText = " ";
            rtb.Focus();
        }

        string LinkAt(int idx)
        {
            var text = rtb.Text;
            if (idx < 0 || idx >= text.Length) return null;
            int s = text.LastIndexOf("[[", Math.Min(idx + 1, text.Length - 1), StringComparison.Ordinal);
            if (s < 0) return null;
            int e = text.IndexOf("]]", s + 2, StringComparison.Ordinal);
            if (e < 0 || e + 1 < idx) return null;
            var inner = text.Substring(s + 2, e - s - 2);
            if (inner.Length == 0 || inner.Contains('\n') || inner.Contains("[[")) return null;
            return inner;
        }

        void OnRtbMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || ModifierKeys != Keys.Control) return;
            var title = LinkAt(rtb.GetCharIndexFromPosition(e.Location));
            if (title != null) { SaveNow(); main.OpenByTitle(title); }
        }

        void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (!e.Control) return;
            switch (e.KeyCode)
            {
                case Keys.B: Toggle(CFM_BOLD); e.SuppressKeyPress = true; break;
                case Keys.I: Toggle(CFM_ITALIC); e.SuppressKeyPress = true; break;
                case Keys.U: Toggle(CFM_UNDERLINE); e.SuppressKeyPress = true; break;
                case Keys.D1: SetBlock(20f, true); e.SuppressKeyPress = true; break;
                case Keys.D2: SetBlock(15f, true); e.SuppressKeyPress = true; break;
                case Keys.D0: SetBlock(12f, false); e.SuppressKeyPress = true; break;
                case Keys.S: SaveNow(); e.SuppressKeyPress = true; break;
                case Keys.Enter:
                    var t = LinkAt(rtb.SelectionStart) ?? LinkAt(rtb.SelectionStart - 1);
                    if (t != null) { e.SuppressKeyPress = true; SaveNow(); main.OpenByTitle(t); }
                    break;
            }
        }

        string Prompt(string label, string def)
        {
            using (var f = new Form { Text = "便签本", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, ClientSize = new Size(UI.S(360), UI.S(116)), Font = UI.F(10.5f), BackColor = UI.Bg })
            {
                var l = new Label { Text = label, Left = UI.S(12), Top = UI.S(12), AutoSize = true, ForeColor = UI.Ink };
                var tb = new TextBox { Left = UI.S(12), Top = UI.S(40), Width = UI.S(336), Text = def };
                var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Left = UI.S(188), Top = UI.S(76), Width = UI.S(76), Height = UI.S(30) };
                var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Left = UI.S(272), Top = UI.S(76), Width = UI.S(76), Height = UI.S(30) };
                f.Controls.AddRange(new Control[] { l, tb, ok, cancel });
                f.AcceptButton = ok; f.CancelButton = cancel;
                return f.ShowDialog(this) == DialogResult.OK ? tb.Text.Trim() : null;
            }
        }

        public void SaveNow()
        {
            if (!dirty) return;
            try { Store.WriteText(Store.NotePath(page.Id), rtb.Rtf); dirty = false; main.PageChanged(); main.SaveState(); } catch { }
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); rtb.Focus(); }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SaveNow();
                if (saveTimer != null) saveTimer.Dispose();
                if (Toolbar != null) Toolbar.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
